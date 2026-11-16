// root/selfhost/src/NetForge.Api/FinancialAccounting.cs
// All posting operations use PostgreSQL transactions and balanced immutable journals.
using System.Globalization;
using System.Security.Claims;
using Microsoft.VisualBasic.FileIO;
using Npgsql;

namespace NetForge.Api;

public sealed record PaymentPostInput(Guid InvoiceId, decimal Amount, string Reference, Guid? RequestId = null);
public sealed record BankUploadInput(string BankName, string Filename, string Csv, bool Confirm);
public sealed record BankAllocationInput(Guid InvoiceId, decimal Amount, Guid RequestId);
public sealed record PaymentReversalInput(Guid RequestId, string Reason);
public sealed record CreditNoteInput(Guid InvoiceId, string Number, decimal Amount, string Reason);

internal sealed record CsvBankLine(DateOnly Date, decimal Amount, string Description,
    string Reference, string TransactionId);

public static class FinancialAccounting
{
    private static bool Admin(HttpContext ctx) => ctx.User.IsInRole("admin");
    private static IResult Forbidden() => Results.Problem("Administrator permissions required",statusCode:403);
    private static string Actor(HttpContext ctx) => ctx.User.Identity?.Name??"unknown";
    private static NpgsqlParameter P(string key, object? value) => Store.P(key,value);
    private static async Task<List<Dictionary<string,object?>>> Rows(
        NpgsqlConnection c,NpgsqlTransaction tx,string sql,params NpgsqlParameter[] values) =>
        await NetworkManagement.Query(c,tx,sql,values);
    private static Task Exec(NpgsqlConnection c,NpgsqlTransaction tx,string sql,
        params NpgsqlParameter[] values) =>
        NetworkManagement.Execute(c,tx,sql,values);

    private static async Task Journal(NpgsqlConnection conn,NpgsqlTransaction tx,
        string type,Guid source,string memo,string actor,string debitAccount,string creditAccount,
        decimal amount)
    {
        if(amount<=0)throw new InvalidOperationException("Journal amount must be positive");
        var rows=await Rows(conn,tx,"""
            INSERT INTO ledger_journals(source_type,source_id,memo,posted_by)
            VALUES (@type,@source,@memo,@actor) RETURNING id
            """,P("type",type),P("source",source),P("memo",memo),P("actor",actor));
        var id=(Guid)rows.Single()["id"]!;
        await Exec(conn,tx,"""
            INSERT INTO ledger_lines(journal_id,line_no,account_code,debit,credit) VALUES
            (@id,1,@debit,@amount,0),(@id,2,@credit,0,@amount)
            """,P("id",id),P("debit",debitAccount),P("credit",creditAccount),
            P("amount",amount));
    }

    private static async Task Audit(NpgsqlConnection conn,NpgsqlTransaction tx,
        string actor,string action,string entity,string id) =>
        await NetworkManagement.Audit(conn,tx,actor,action,entity,id);

    private static async Task<Dictionary<string,object?>?> LockedInvoice(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)
    {
        var rows=await Rows(conn,tx,
            "SELECT id,status,total,customer_id,number FROM invoices WHERE id=@id FOR UPDATE",
            P("id",id));
        return rows.FirstOrDefault();
    }

    private static async Task<Dictionary<string,object?>> Balance(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)
    {
        var rows=await Rows(conn,tx,
            "SELECT paid,credited,outstanding FROM invoice_balances WHERE invoice_id=@id",
            P("id",id));
        return rows.Single();
    }

    private static async Task<decimal> RefreshStatus(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)
    {
        var row=await Balance(conn,tx,id);
        var remaining=(decimal)row["outstanding"]!;
        var credited=(decimal)row["credited"]!;
        var newStatus=remaining==0 ? (credited>0 ? "settled":"paid") : "issued";
        await Exec(conn,tx,"UPDATE invoices SET status=@status WHERE id=@id",
            P("status",newStatus),P("id",id));
        return remaining;
    }

    public static async Task<IResult> Issue(Guid id,HttpContext ctx,NpgsqlDataSource db)
    {
        if(!Admin(ctx))return Forbidden();
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var invoice=await LockedInvoice(conn,(NpgsqlTransaction)tx,id);
        if(invoice is null || ((string)invoice["status"]!) != "draft")
            return Results.Conflict(new{error="Invoice must exist and be a draft"});
        var total=(decimal)invoice["total"]!;
        await Exec(conn,(NpgsqlTransaction)tx,
            "UPDATE invoices SET status=@status WHERE id=@id",
            P("status",total==0?"settled":"issued"),P("id",id));
        if(total>0)
            await Journal(conn,(NpgsqlTransaction)tx,"invoice",id,
                "Issued invoice "+invoice["number"],Actor(ctx),
                "AR","REVENUE",total);
        await Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),"invoice-issue","invoice",id.ToString());
        await tx.CommitAsync();
        return Results.Ok(new{ok=true,status=total==0?"settled":"issued"});
    }

    private static async Task<(Guid PaymentId,decimal Remaining)> PostPayment(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid invoiceId,decimal amount,
        string reference,Guid requestId,Guid? bankLineId,string actor)
    {
        var id=Guid.NewGuid();
        await Exec(conn,tx,"""
            INSERT INTO payments(id,invoice_id,amount,reference,request_id,bank_statement_line_id)
            VALUES(@id,@invoice,@amount,@reference,@request,@line)
            """,P("id",id),P("invoice",invoiceId),P("amount",amount),
            P("reference",reference),P("request",requestId),P("line",bankLineId));
        await Journal(conn,tx,"payment",id,"Payment "+reference,actor,
            "BANK_CLEARING","AR",amount);
        var remaining=await RefreshStatus(conn,tx,invoiceId);
        await Audit(conn,tx,actor,bankLineId is null?"payment-manual":"payment-bank-allocated",
            "payment",id.ToString());
        return(id,remaining);
    }

    public static async Task<IResult> ManualPayment(
        HttpContext ctx,NpgsqlDataSource db,PaymentPostInput input)
    {
        if(!Admin(ctx))return Forbidden();
        var reference=(input.Reference??"").Trim();
        if(input.Amount<=0 || input.Amount>9999999999.99m ||
            reference.Length is < 1 or > 160)
            return Results.BadRequest(new{error="Positive amount and reference (1–160 characters) required"});
        var requestId=input.RequestId is { } provided && provided!=Guid.Empty
            ? provided : Guid.NewGuid();
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var invoice=await LockedInvoice(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        if(invoice is null)return Results.NotFound();
        // Idempotency is anchored to a unique request ID on payments.
        var prior=await Rows(conn,(NpgsqlTransaction)tx,
            "SELECT id,invoice_id,amount FROM payments WHERE request_id=@request",
            P("request",requestId));
        if(prior.Count!=0)
        {
            var p=prior.Single();
            if((Guid)p["invoice_id"]! != input.InvoiceId ||
               (decimal)p["amount"]! != input.Amount)
                return Results.Conflict(new{error="Request ID was previously used for different payment details"});
            return Results.Ok(new{ok=true,paymentId=p["id"],replayed=true});
        }
        if((string)invoice["status"]! is not ("issued" or "paid" or "settled"))
            return Results.Conflict(new{error="Invoice must be issued before recording payment"});
        var balance=await Balance(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        if(input.Amount>(decimal)balance["outstanding"]!)
            return Results.Conflict(new{error="Payment exceeds invoice outstanding balance"});
        var duplicate=await Rows(conn,(NpgsqlTransaction)tx,"""
            SELECT p.id FROM payments p
            LEFT JOIN payment_reversals r ON r.payment_id=p.id
            WHERE p.invoice_id=@invoice AND p.reference=@reference
              AND p.amount=@amount AND r.id IS NULL LIMIT 1
            """,P("invoice",input.InvoiceId),P("reference",reference),P("amount",input.Amount));
        if(duplicate.Count>0)
            return Results.Conflict(new{error="A payment with the same reference and amount is already recorded for this invoice"});
        var result=await PostPayment(conn,(NpgsqlTransaction)tx,input.InvoiceId,
            input.Amount,reference,requestId,null,Actor(ctx));
        await tx.CommitAsync();
        return Results.Ok(new{ok=true,paymentId=result.PaymentId,
            remaining=result.Remaining,requestId,replayed=false});
    }

    public static async Task<IResult> ReversePayment(Guid paymentId,
        HttpContext ctx,NpgsqlDataSource db,PaymentReversalInput input)
    {
        if(!Admin(ctx))return Forbidden();
        if(input.RequestId==Guid.Empty || (input.Reason??"").Trim().Length is < 8 or > 300)
            return Results.BadRequest(new{error="Unique request ID and reversal reason (8–300 characters) required"});
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var original=await Rows(conn,(NpgsqlTransaction)tx,
            "SELECT invoice_id FROM payments WHERE id=@id",P("id",paymentId));
        if(original.Count==0)return Results.NotFound();
        var invoiceId=(Guid)original.Single()["invoice_id"]!;
        await LockedInvoice(conn,(NpgsqlTransaction)tx,invoiceId);
        var existing=await Rows(conn,(NpgsqlTransaction)tx,"""
            SELECT id,request_id FROM payment_reversals WHERE payment_id=@payment
            """,P("payment",paymentId));
        if(existing.Count>0)
        {
            var r=existing.Single();
            if((Guid)r["request_id"]! == input.RequestId)
                return Results.Ok(new{ok=true,reversalId=r["id"],replayed=true});
            return Results.Conflict(new{error="This payment was already reversed"});
        }
        var payment=await Rows(conn,(NpgsqlTransaction)tx,
            "SELECT amount,reference FROM payments WHERE id=@id FOR UPDATE",
            P("id",paymentId));
        var amount=(decimal)payment.Single()["amount"]!;
        var reversal=Guid.NewGuid();
        await Exec(conn,(NpgsqlTransaction)tx,"""
            INSERT INTO payment_reversals(id,payment_id,request_id,reason,reversed_by)
            VALUES(@id,@payment,@request,@reason,@actor)
            """,P("id",reversal),P("payment",paymentId),P("request",input.RequestId),
            P("reason",input.Reason!.Trim()),P("actor",Actor(ctx)));
        await Journal(conn,(NpgsqlTransaction)tx,"payment_reversal",reversal,
            "Reversal of payment "+paymentId,Actor(ctx),"AR","BANK_CLEARING",amount);
        var remaining=await RefreshStatus(conn,(NpgsqlTransaction)tx,invoiceId);
        await Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),"payment-reverse",
            "payment",paymentId.ToString());
        await tx.CommitAsync();
        return Results.Ok(new{ok=true,reversalId=reversal,
            remaining,replayed=false});
    }

    public static async Task<IResult> Credit(
        HttpContext ctx,NpgsqlDataSource db,CreditNoteInput input)
    {
        if(!Admin(ctx))return Forbidden();
        var number=(input.Number??"").Trim();
        var reason=(input.Reason??"").Trim();
        if(input.Amount<=0 || number.Length is < 1 or > 120 ||
            reason.Length is < 8 or > 300)
            return Results.BadRequest(new{error="Credit amount, number and reason (8–300 characters) required"});
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var invoice=await LockedInvoice(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        if(invoice is null)return Results.NotFound();
        if((string)invoice["status"]! is not ("issued" or "paid" or "settled"))
            return Results.Conflict(new{error="Only issued invoices can receive credit notes"});
        var balance=await Balance(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        if(input.Amount>(decimal)balance["outstanding"]!)
            return Results.Conflict(new{error="Credit exceeds invoice outstanding balance"});
        var creditId=Guid.NewGuid();
        await Exec(conn,(NpgsqlTransaction)tx,"""
            INSERT INTO credit_notes(id,invoice_id,number,amount,reason,created_by)
            VALUES(@id,@invoice,@number,@amount,@reason,@actor)
            """,P("id",creditId),P("invoice",input.InvoiceId),P("number",number),
            P("amount",input.Amount),P("reason",reason),P("actor",Actor(ctx)));
        await Journal(conn,(NpgsqlTransaction)tx,"credit_note",creditId,
            "Credit note "+number,Actor(ctx),"REVENUE_RETURNS","AR",input.Amount);
        var remaining=await RefreshStatus(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        await Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),"credit-note",
            "invoice",input.InvoiceId.ToString());
        await tx.CommitAsync();
        return Results.Created($"/api/billing/credits/{creditId}",
            new{id=creditId,remaining});
    }

    private static List<CsvBankLine> ParseCsv(string csv)
    {
        if(string.IsNullOrWhiteSpace(csv) ||
           System.Text.Encoding.UTF8.GetByteCount(csv)>250_000)
            throw new ArgumentException("CSV must be nonempty and at most 250 KB");
        using var reader=new StringReader(csv);
        using var parser=new TextFieldParser(reader);
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes=true;
        parser.TrimWhiteSpace=false;
        var header=parser.ReadFields()??[];
        var expected=new[]{"Date","Amount","Description","Reference","TransactionId"};
        if(header.Length!=5 || !header.Select(x=>x.Trim().TrimStart('\uFEFF'))
            .SequenceEqual(expected,StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("CSV header must be Date,Amount,Description,Reference,TransactionId");
        var result=new List<CsvBankLine>();
        while(!parser.EndOfData)
        {
            string[] values;
            try {values=parser.ReadFields()??[];}
            catch(MalformedLineException){throw new ArgumentException("Malformed quoted CSV line");}
            if(values.Length==0 || values.All(string.IsNullOrWhiteSpace))continue;
            if(values.Length!=5 || result.Count>=1000)
                throw new ArgumentException("Each row must have exactly five fields (maximum 1000 rows)");
            if(!DateOnly.TryParseExact(values[0].Trim(),"yyyy-MM-dd",
                CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
                throw new ArgumentException("Date must use YYYY-MM-DD");
            if(!decimal.TryParse(values[1].Trim(),NumberStyles.AllowLeadingSign|
                NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount) ||
                amount==0 || amount!=decimal.Round(amount,2) || Math.Abs(amount)>9999999999.99m)
                throw new ArgumentException("Amount must be a signed nonzero ZAR decimal with two or fewer decimal places");
            var description=values[2].Trim();
            var reference=values[3].Trim();
            var transactionId=values[4].Trim();
            if(description.Length>400 || reference.Length>200 ||
               transactionId.Length is < 1 or > 120)
                throw new ArgumentException("Description, reference or transaction ID is too long/empty");
            result.Add(new CsvBankLine(date,amount,description,reference,transactionId));
        }
        if(result.Count==0)throw new ArgumentException("CSV has no data rows");
        return result;
    }

    private static bool ValidBank(BankUploadInput input) =>
        (input.BankName??"").Trim().Length is >= 2 and <= 100 &&
        (input.Filename??"").Trim().Length is >= 1 and <= 180;

    public static async Task<IResult> BankPreview(
        HttpContext ctx,NpgsqlDataSource db,BankUploadInput input)
    {
        if(!Admin(ctx))return Forbidden();
        if(!ValidBank(input))return Results.BadRequest(new{error="Bank name and filename required"});
        List<CsvBankLine> rows;
        try{rows=ParseCsv(input.Csv??"");}
        catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        var ids=rows.Select(x=>x.TransactionId).ToArray();
        var duplicates=await Store.Rows(db,"""
            SELECT transaction_id FROM bank_statement_lines
            WHERE bank_name=@bank AND transaction_id=ANY(@ids)
            """,P("bank",input.BankName.Trim()),P("ids",ids));
        var found=duplicates.Select(x=>(string)x["transaction_id"]!)
            .ToHashSet(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var preview=rows.Select(x=>new{
            x.Date,x.Amount,x.Description,x.Reference,x.TransactionId,
            duplicate=found.Contains(x.TransactionId)||!seen.Add(x.TransactionId),
            allocatable=x.Amount>0
        }).ToArray();
        return Results.Ok(new{count=preview.Length,duplicateCount=preview.Count(x=>x.duplicate),
            rows=preview,note="Preview only; no bank records or customer invoices were changed."});
    }

    public static async Task<IResult> BankImport(
        HttpContext ctx,NpgsqlDataSource db,BankUploadInput input)
    {
        if(!Admin(ctx))return Forbidden();
        if(!input.Confirm)return Results.BadRequest(new{error="Explicit import confirmation required"});
        if(!ValidBank(input))return Results.BadRequest(new{error="Bank name and filename required"});
        List<CsvBankLine> rows;
        try{rows=ParseCsv(input.Csv??"");}
        catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var batch=Guid.NewGuid();
        await Exec(conn,(NpgsqlTransaction)tx,"""
            INSERT INTO bank_import_batches(id,bank_name,filename,imported_by)
            VALUES(@id,@bank,@file,@actor)
            """,P("id",batch),P("bank",input.BankName.Trim()),
            P("file",input.Filename.Trim()),P("actor",Actor(ctx)));
        var inserted=0;
        foreach(var row in rows)
        {
            var n=await Rows(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO bank_statement_lines(batch_id,bank_name,transaction_id,
                    value_date,amount,description,reference)
                VALUES(@batch,@bank,@txid,@date,@amount,@description,@reference)
                ON CONFLICT(bank_name,transaction_id) DO NOTHING RETURNING id
                """,P("batch",batch),P("bank",input.BankName.Trim()),
                P("txid",row.TransactionId),P("date",row.Date),
                P("amount",row.Amount),P("description",row.Description),
                P("reference",row.Reference));
            inserted+=n.Count;
        }
        if(inserted==0)
        {
            await tx.RollbackAsync();
            return Results.Ok(new{imported=0,skipped=rows.Count,
                note="All transactions already imported; no changes made."});
        }
        await Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),"bank-import",
            "bank_import",batch.ToString());
        await tx.CommitAsync();
        return Results.Ok(new{batchId=batch,imported=inserted,skipped=rows.Count-inserted,
            note="Transactions imported as unmatched bank lines; no invoice allocation occurred."});
    }

    public static async Task<IResult> Allocate(
        Guid bankLineId,HttpContext ctx,NpgsqlDataSource db,BankAllocationInput input)
    {
        if(!Admin(ctx))return Forbidden();
        if(input.RequestId==Guid.Empty || input.Amount<=0 ||
           input.Amount>9999999999.99m || input.Amount!=decimal.Round(input.Amount,2))
            return Results.BadRequest(new{error="Unique request ID and positive two-decimal amount required"});
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        // Lock bank line first; all bank allocations acquire this lock before an
        // invoice lock to prevent overspending the same imported transaction.
        var bank=await Rows(conn,(NpgsqlTransaction)tx,"""
            SELECT id,transaction_id,amount,bank_name FROM bank_statement_lines
            WHERE id=@id FOR UPDATE
            """,P("id",bankLineId));
        if(bank.Count==0)return Results.NotFound();
        var line=bank.Single();
        if((decimal)line["amount"]!<=0)
            return Results.Conflict(new{error="Only incoming bank credits can be allocated to invoices"});
        var invoice=await LockedInvoice(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        if(invoice is null)return Results.NotFound();
        var existing=await Rows(conn,(NpgsqlTransaction)tx,
            "SELECT id,invoice_id,amount,bank_statement_line_id FROM payments WHERE request_id=@id",
            P("id",input.RequestId));
        if(existing.Count>0)
        {
            var p=existing.Single();
            if((Guid)p["invoice_id"]! != input.InvoiceId ||
               (decimal)p["amount"]! != input.Amount ||
               (Guid?)p["bank_statement_line_id"] != bankLineId)
                return Results.Conflict(new{error="Request ID used for different allocation"});
            return Results.Ok(new{ok=true,paymentId=p["id"],replayed=true});
        }
        if((string)invoice["status"]! is not ("issued" or "paid" or "settled"))
            return Results.Conflict(new{error="Allocate only to issued invoices"});
        var availableRows=await Rows(conn,(NpgsqlTransaction)tx,"""
            SELECT COALESCE(sum(p.amount),0) AS allocated FROM payments p
            LEFT JOIN payment_reversals r ON r.payment_id=p.id
            WHERE p.bank_statement_line_id=@line AND r.id IS NULL
            """,P("line",bankLineId));
        var available=(decimal)line["amount"]!-(decimal)availableRows.Single()["allocated"]!;
        if(input.Amount>available)
            return Results.Conflict(new{error="Allocation exceeds unallocated bank receipt"});
        var balance=await Balance(conn,(NpgsqlTransaction)tx,input.InvoiceId);
        if(input.Amount>(decimal)balance["outstanding"]!)
            return Results.Conflict(new{error="Allocation exceeds invoice outstanding balance"});
        var reference="BANK:"+line["bank_name"]+":"+line["transaction_id"];
        if(reference.Length>160)
            reference=reference[..160];
        var result=await PostPayment(conn,(NpgsqlTransaction)tx,input.InvoiceId,
            input.Amount,reference,input.RequestId,bankLineId,Actor(ctx));
        await tx.CommitAsync();
        return Results.Ok(new{ok=true,paymentId=result.PaymentId,
            remaining=result.Remaining,bankRemaining=available-input.Amount,replayed=false});
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/billing/ledger",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT j.id,j.posted_at,j.source_type,j.source_id,j.memo,j.posted_by,
                       l.line_no,l.account_code,l.debit,l.credit
                FROM ledger_journals j JOIN ledger_lines l ON l.journal_id=j.id
                ORDER BY j.posted_at DESC,j.id,l.line_no LIMIT 1500
                """));
        });
        api.MapGet("/billing/trial-balance",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT account_code,COALESCE(sum(debit),0) AS debit,
                       COALESCE(sum(credit),0) AS credit,
                       COALESCE(sum(debit-credit),0) AS net
                FROM ledger_lines GROUP BY account_code ORDER BY account_code
                """));
        });
        api.MapGet("/billing/credits",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT c.id,c.invoice_id,i.number AS invoice,c.number,c.amount,
                       c.reason,c.created_by,c.created_at
                FROM credit_notes c JOIN invoices i ON i.id=c.invoice_id
                ORDER BY c.created_at DESC LIMIT 1000
                """));
        });
        api.MapGet("/billing/payments",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.invoice_id,i.number AS invoice,p.amount,p.reference,
                       p.received_at,p.bank_statement_line_id,
                       r.id AS reversal_id,r.reason AS reversal_reason
                FROM payments p JOIN invoices i ON i.id=p.invoice_id
                LEFT JOIN payment_reversals r ON r.payment_id=p.id
                ORDER BY p.received_at DESC LIMIT 1000
                """));
        });
        api.MapGet("/billing/bank-lines",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT l.id,l.bank_name,l.transaction_id,l.value_date,l.amount,
                       l.description,l.reference,l.batch_id,
                       COALESCE(p.allocated,0) AS allocated,
                       CASE WHEN l.amount>0 THEN l.amount-COALESCE(p.allocated,0)
                            ELSE 0 END AS available
                FROM bank_statement_lines l
                LEFT JOIN LATERAL (
                    SELECT sum(p.amount) AS allocated FROM payments p
                    LEFT JOIN payment_reversals r ON r.payment_id=p.id
                    WHERE p.bank_statement_line_id=l.id AND r.id IS NULL
                ) p ON true
                ORDER BY l.value_date DESC,l.created_at DESC LIMIT 3000
                """));
        });
        api.MapPost("/billing/bank-imports/preview",BankPreview);
        api.MapPost("/billing/bank-imports",BankImport);
        api.MapPost("/billing/bank-lines/{id:guid}/allocate",Allocate);
        api.MapPost("/billing/credits",Credit);
        api.MapPost("/billing/payments/{id:guid}/reverse",ReversePayment);
    }
}
