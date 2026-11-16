# root/selfhost/FINANCIAL_OPERATIONS.md
# NetForge — Financial posting and reconciliation (Phase 4)

NetForge uses ASP.NET Core 11, PostgreSQL and an original CRM/AAA interface. This release provides a **transactional, audit-traceable foundation**, not certified statutory accounting, tax invoices or an automated payment gateway.

## Ledger design

Migration `004_financial_ledger_reconciliation.sql` creates `ledger_journals`, `ledger_lines`, `payment_reversals`, `credit_notes`, `bank_import_batches`, `bank_statement_lines`, and the `invoice_balances` view.

Each journal is identified by an immutable `source_type/source_id` pair. The database verifies at transaction commit that every posted journal has at least two lines with equal total debits and credits. Direct updates or deletions are rejected by PostgreSQL triggers. All posting operations run inside a PostgreSQL transaction, and invoices/bank lines are locked before changing balances.

| Event | Debit | Credit |
| --- | --- | --- |
| Issue positive invoice | Accounts Receivable | Revenue |
| Record confirmed payment or allocate bank credit | Bank Clearing | Accounts Receivable |
| Reverse recorded payment | Accounts Receivable | Bank Clearing |
| Post customer credit note | Revenue Returns | Accounts Receivable |

Bank Clearing is **not a bank account balance**. It represents payments entered or allocated in NetForge; it must be reconciled against real bank statements, settlement reports and refunds. Do not assume the clearing balance equals a bank balance.

### Historical data

The first application start after migration 004 records already-issued invoices and historical payment entries as source-tagged `legacy_invoice` and `legacy_payment` journals. These preserve existing amounts but do not claim to reconstruct historical revenue or bank posting dates.

Before upgrading a populated system, reconcile all invoices and payment totals, make a consistent PostgreSQL backup and test restoring it. Previous migrations remain unchanged; migration 004 is run once and its SHA-256 checksum recorded.

## Invoice and payment workflow

1. Create a customer invoice or generate monthly draft invoices in the Billing section. Drafts carry no ledger posting.
2. Review totals before pressing **Issue invoice**. Issuance posts a double-entry journal. There is currently no tax, VAT, proration, contract, PDF invoice or message delivery.
3. For an independently confirmed EFT or cash receipt, record a **manual payment** with its reference. A unique request ID and duplicate-reference protection help prevent repeated entries.
4. The application tracks `paid`, `credited` and `outstanding` separately. Credit notes reduce what is owed without claiming cash was received.
5. If payment information was entered incorrectly, use **Bank Reconciliation → Reverse**. Give an auditable reason; a new reversing journal is recorded. The original receipt remains visible as reversed.
6. Credit notes are permanent in this version. **Do not post one for testing on live data**; corrections need a separately approved compensating invoice.
7. The customer portal shows issued/paid/settled invoices, applied credits and payment reversal state. It does not initiate card/bank transactions.

## Bank CSV import

The importer accepts a **bank-neutral normalized file**, not an arbitrary native export from FNB, Capitec, Nedbank, ABSA, Standard Bank or another provider. Convert exports to:

```csv
Date,Amount,Description,Reference,TransactionId
2026-11-16,200.00,"Customer EFT","ACC-123","BANK-UNIQUE-0001"
2026-11-16,-15.00,"Bank fee","CHARGE","BANK-UNIQUE-0002"
```

- **Date**: ISO `YYYY-MM-DD`.
- **Amount**: ZAR, signed decimal with no grouping separators; positive=incoming, negative=outgoing.
- **Description**: max 400 characters.
- **Reference**: max 200 characters.
- **TransactionId**: stable bank-provided unique identifier, max 120 characters. It must not be substituted with a generated row number that changes on every export.
- Upload limit: 250 KB and 1,000 data lines per preview/import.

1. Open **Bank Reconciliation** in the administrator console.
2. Select the bank/account label and your normalized CSV file.
3. Press **Preview import**. Inspect every row, amount and duplicate indicator.
4. Confirm the import. Rows are saved to the unmatched bank register without changing customer balances or posting any ledger entry.
5. Click **Allocate** on an incoming transaction. Choose the correct issued customer invoice and amount. Partial/split allocations are allowed; combined active allocations cannot exceed the received amount or invoice outstanding.
6. Record a payment reversal for an incorrect allocation; the original row and audit trail remain, and the bank line becomes available again.

**No automatic matching or bank API is implemented.** Validate each bank transaction ID against the original statement outside NetForge and obtain accounting approval for credit notes, customer allocation corrections, or reversal of payments.

## Safety and failure modes

- **Duplicate uploads:** bank name plus transaction ID is unique. Re-importing the same statement skips already-imported lines.
- **Request retry:** payment/allocation requests accept a UUID idempotency key. A repeated request returns the existing entry instead of creating another.
- **Concurrent posting:** PostgreSQL invoice/bank-line row locks prevent two API workers from allocating more than the available balances.
- **Reversals:** payment amounts are never deleted; a new reversal record excludes them from balances and posts the opposite journal.
- **Credit notes:** cannot exceed the invoice outstanding balance and are not interchangeable with refunds or tax credits.
- **Server crash during posting:** PostgreSQL rolls back the entire posting operation, including journal, payment and audit rows.
- **External manipulation:** database roles should separate application write privileges, reporting reads and FreeRADIUS SQL access. The current Compose configuration still uses a shared database account and needs least-privilege hardening before real financial data is held.
- **Automated suspension:** uses the same effective outstanding balances and remains disabled by default. Cash reversals can cause a settled invoice to reopen; credit notes can settle an overdue invoice without bank receipt. Review suspension consequences before enabling debt enforcement.
- **Tax compliance:** no claim is made that automatically generated totals, journal lines, or invoice numbers satisfy SARS invoicing or POPIA requirements.

## API inventory

All posting endpoints require a logged-in administrator with CSRF token.

| Endpoint | Purpose |
| --- | --- |
| `POST /api/invoices/{id}/issue` | Post invoice revenue/AR |
| `POST /api/payments` | Post manual confirmed payment |
| `POST /api/billing/payments/{id}/reverse` | Append-only payment reversal |
| `POST /api/billing/credits` | Post credit note |
| `POST /api/billing/bank-imports/preview` | Parse/validate CSV with duplicate indications; no DB mutation |
| `POST /api/billing/bank-imports` | Confirm normalized statement import |
| `GET /api/billing/bank-lines` | Imported bank lines and remaining allocatable amounts |
| `POST /api/billing/bank-lines/{id}/allocate` | Record manual invoice allocation and journal |
| `GET /api/billing/trial-balance` | Per-account debit/credit totals |
| `GET /api/billing/ledger` | Source-tagged immutable journal lines |
| `GET /api/billing/payments` | Posted and reversed receipts |
| `GET /api/billing/credits` | Posted credit note register |

## Acceptance gates

GitHub Actions validates .NET/JavaScript compilation, ledger posting with the real PostgreSQL database, invoice balance arithmetic, duplicate import prevention, request retry, split allocations, payment reversal, credit notes and accounting balance. FreeRADIUS authentication tests must also continue passing.

Prior to production use, additionally test a restored database with existing customer data, simulate two concurrent allocations of the same line, verify the bank's stable transaction IDs, review tax treatment, and perform a complete accountant-approved reconciliation.
