// root/selfhost/src/NetForge.Api/AssuranceCredits.cs
// No automatic SLA refunds: proposal, independent admin review, and explicit
// invoice-credit posting are separate, audited and transactionally idempotent.
using Npgsql;

namespace NetForge.Api;

public sealed record SlaCreditProposalInput(Guid ServiceId,Guid InterruptionId,
    Guid SlaVersionId,Guid InvoiceId,decimal Amount,string Reason,bool Confirm);
public sealed record SlaCreditReviewInput(bool Approve,string Reason,bool Confirm);
public sealed record SlaCreditPostInput(bool Confirm);

public static class AssuranceCredits
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Staff(HttpContext ctx)=>AssuranceAgreements.Staff(ctx);
    private static bool Admin(HttpContext ctx)=>AssuranceAgreements.Admin(ctx);
    private static IResult Denied()=>AssuranceAgreements.Denied();
    private static IResult Invalid(string reason)=>AssuranceAgreements.Invalid(reason);
    private static IResult Conflict(string reason)=>AssuranceAgreements.Conflict(reason);
    private static string Actor(HttpContext ctx)=>AssuranceAgreements.Actor(ctx);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assurance/credit-requests",async(HttpContext ctx,
            NpgsqlDataSource db,string? status)=>
        {
            if(!Staff(ctx))return Denied();
            if(status is not (null or "all" or "proposed" or "approved" or "rejected" or "posted"))
                return Invalid("status must be proposed, approved, rejected, posted or all");
            return Results.Ok(await Store.Rows(db,"""
                SELECT r.id,r.service_id,s.username,c.account,r.invoice_id,
                    i.number AS invoice,r.interruption_id,r.sla_version_id,
                    t.version AS sla_version,t.agreement_reference,
                    r.amount,r.reason,r.status,r.proposed_by,r.proposed_at,
                    r.reviewed_by,r.reviewed_at,r.review_reason,
                    r.posted_by,r.posted_at,r.credit_note_id
                FROM assurance_credit_requests r
                JOIN services s ON s.id=r.service_id
                JOIN customers c ON c.id=s.customer_id
                JOIN invoices i ON i.id=r.invoice_id
                JOIN assurance_sla_versions t ON t.id=r.sla_version_id
                WHERE (@status::text IS NULL OR @status='all' OR r.status=@status)
                ORDER BY r.proposed_at DESC LIMIT 1000
                """,P("status",status)));
        });

        api.MapPost("/assurance/credit-requests",async(HttpContext ctx,
            NpgsqlDataSource db,SlaCreditProposalInput input)=>
        {
            if(!Staff(ctx))return Denied();
            var reason=(input.Reason??"").Trim();
            if(!input.Confirm || input.ServiceId==Guid.Empty ||
               input.InterruptionId==Guid.Empty || input.SlaVersionId==Guid.Empty ||
               input.InvoiceId==Guid.Empty || input.Amount<=0 ||
               input.Amount>9999999999.99m ||
               decimal.Round(input.Amount,2)!=input.Amount ||
               reason.Length is <12 or >300)
                return Invalid("Confirm positive ZAR cents, service, verified interruption, SLA version, invoice and reason 12–300 chars");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            // Lock invoice to synchronize proposal and real credit postings.
            var lines=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT i.customer_id AS invoice_customer,i.status AS invoice_status,
                       i.total,b.outstanding,s.customer_id AS service_customer,
                       a.started_at,a.ended_at,t.effective_from,
                       t.credit_cap_percent,
                       EXISTS(SELECT 1 FROM assurance_sla_versions newer
                         WHERE newer.service_id=s.id
                           AND newer.effective_from>t.effective_from
                           AND newer.effective_from<=a.started_at::date) AS stale_terms
                FROM services s
                JOIN assurance_interruptions a ON a.service_id=s.id
                JOIN assurance_sla_versions t ON t.service_id=s.id
                JOIN invoices i ON i.customer_id=s.customer_id
                JOIN invoice_balances b ON b.invoice_id=i.id
                WHERE s.id=@service AND a.id=@interruption
                  AND t.id=@terms AND i.id=@invoice
                FOR UPDATE OF i
                """,P("service",input.ServiceId),P("interruption",input.InterruptionId),
                P("terms",input.SlaVersionId),P("invoice",input.InvoiceId));
            if(lines.Count==0)return Conflict("Source interruption, SLA terms and invoice must belong to the same subscriber service/customer");
            var data=lines.Single();
            if((string)data["invoice_status"]! is not ("issued" or "paid" or "settled"))
                return Conflict("Invoice must be issued before requesting a credit");
            if((DateOnly)data["effective_from"]!>
               DateOnly.FromDateTime((DateTime)data["started_at"]!) ||
               data["stale_terms"] is true)
                return Conflict("Selected SLA version was not effective when the interruption began");
            var cap=decimal.Round((decimal)data["total"]!*
                (decimal)data["credit_cap_percent"]!/100m,2);
            if(input.Amount>cap || cap<=0)
                return Conflict("Proposed credit exceeds the chosen SLA version's invoice percentage cap");
            if(input.Amount>(decimal)data["outstanding"]!)
                return Conflict("Proposed credit exceeds currently outstanding invoice balance");
            var duplicates=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id FROM assurance_credit_requests
                WHERE interruption_id=@interruption AND invoice_id=@invoice
                  AND status IN ('proposed','approved','posted') LIMIT 1
                """,P("interruption",input.InterruptionId),P("invoice",input.InvoiceId));
            if(duplicates.Count>0)return Conflict("A credit proposal already exists for that interruption and invoice");
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO assurance_credit_requests(service_id,interruption_id,
                    sla_version_id,invoice_id,amount,reason,proposed_by)
                VALUES(@service,@interruption,@terms,@invoice,@amount,@reason,@actor)
                RETURNING id
                """,P("service",input.ServiceId),P("interruption",input.InterruptionId),
                P("terms",input.SlaVersionId),P("invoice",input.InvoiceId),
                P("amount",input.Amount),P("reason",reason),P("actor",Actor(ctx)));
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "assurance-credit-proposed","assurance_credit",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/assurance/credit-requests/"+id,
                new{id,status="proposed",ledgerPosted=false,
                    note="Proposal only; a different administrator must review, and posting remains a distinct explicit operation."});
        });

        api.MapPost("/assurance/credit-requests/{id:guid}/review",async(
            Guid id,HttpContext ctx,NpgsqlDataSource db,SlaCreditReviewInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var reason=(input.Reason??"").Trim();
            if(!input.Confirm || reason.Length is <8 or >300)
                return Invalid("Review requires explicit confirmation and reason 8–300 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id,status,proposed_by FROM assurance_credit_requests
                WHERE id=@id FOR UPDATE
                """,P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var record=rows.Single();
            if((string)record["status"]! != "proposed")
                return Conflict("Only proposed credit requests may be reviewed");
            if(string.Equals((string)record["proposed_by"]!,Actor(ctx),
                StringComparison.OrdinalIgnoreCase))
                return Conflict("The person who proposed this credit cannot approve or reject it");
            var status=input.Approve?"approved":"rejected";
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE assurance_credit_requests SET status=@status,reviewed_by=@actor,
                    reviewed_at=now(),review_reason=@reason WHERE id=@id
                """,P("id",id),P("status",status),P("actor",Actor(ctx)),P("reason",reason));
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "assurance-credit-"+status,"assurance_credit",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,status,ledgerPosted=false});
        });

        api.MapPost("/assurance/credit-requests/{id:guid}/post",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,SlaCreditPostInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)
                return Invalid("Confirm posting the approved credit to the immutable financial ledger");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id,status,invoice_id,amount,reason,proposed_by,
                    reviewed_by,credit_note_id
                FROM assurance_credit_requests WHERE id=@id FOR UPDATE
                """,P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var request=rows.Single();
            if((string)request["status"]!=="posted")
                return Results.Ok(new{ok=true,status="posted",creditNoteId=request["credit_note_id"],
                    replayed=true,note="Previously posted; no additional financial entry was created."});
            if((string)request["status"]! != "approved" ||
               request["reviewed_by"] is null ||
               string.Equals((string)request["reviewed_by"]!,
                   (string)request["proposed_by"]!,StringComparison.OrdinalIgnoreCase))
                return Conflict("A different administrator must approve this request before ledger posting");
            var invoiceId=(Guid)request["invoice_id"]!;
            var invoices=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id,status FROM invoices WHERE id=@id FOR UPDATE
                """,P("id",invoiceId));
            if(invoices.Count==0)return Results.NotFound();
            if((string)invoices.Single()["status"]! is not ("issued" or "paid" or "settled"))
                return Conflict("Invoice is no longer issued");
            var balance=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT outstanding FROM invoice_balances WHERE invoice_id=@id
                """,P("id",invoiceId));
            var amount=(decimal)request["amount"]!;
            if(amount>(decimal)balance.Single()["outstanding"]!)
                return Conflict("Invoice balance changed since approval; do not post an excess credit");
            var creditId=Guid.NewGuid();
            var number="NF-SLA-"+id.ToString("N");
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO credit_notes(id,invoice_id,number,amount,reason,created_by)
                VALUES(@id,@invoice,@number,@amount,@reason,@actor)
                """,P("id",creditId),P("invoice",invoiceId),P("number",number),
                P("amount",amount),P("reason",(string)request["reason"]!),
                P("actor",Actor(ctx)));
            await FinancialAccounting.PostJournal(conn,(NpgsqlTransaction)tx,
                "credit_note",creditId,"Approved service assurance credit "+number,
                Actor(ctx),"REVENUE_RETURNS","AR",amount);
            var refreshed=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT credited,outstanding FROM invoice_balances WHERE invoice_id=@id
                """,P("id",invoiceId));
            var remaining=(decimal)refreshed.Single()["outstanding"]!;
            var credited=(decimal)refreshed.Single()["credited"]!;
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
                "UPDATE invoices SET status=@status WHERE id=@id",
                P("id",remaining==0?(credited>0?"settled":"paid"):"issued"));
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE assurance_credit_requests
                SET status='posted',posted_by=@actor,posted_at=now(),credit_note_id=@credit
                WHERE id=@id
                """,P("id",id),P("actor",Actor(ctx)),P("credit",creditId));
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "assurance-credit-posted","assurance_credit",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,status="posted",creditNoteId=creditId,
                remaining,ledgerPosted=true,replayed=false});
        });
    }
}
