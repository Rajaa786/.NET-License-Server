using System;
using System.Collections.Generic;

namespace MyLanService.Core
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Role { get; set; } = "CA";
        public string Password { get; set; }
        public DateTime DateJoined { get; set; }
        public DateTime ExpiryDate { get; set; }
        public DateTime? LastLogin { get; set; }

        public ICollection<Case> Cases { get; set; }
    }

    public class Case
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public string Status { get; set; }
        public int Pages { get; set; } = 0;
        public DateTime CreatedAt { get; set; }
        public bool Deleted { get; set; } = false;

        public ICollection<Eod> Eods { get; set; }
        public ICollection<FailedStatement> FailedStatements { get; set; }
        public ICollection<OpportunityToEarn> Opportunities { get; set; }
        public ICollection<Statement> Statements { get; set; }
        public ICollection<Summary> Summaries { get; set; }
    }

    public class Category
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class Eod
    {
        public int Id { get; set; }
        public int CaseId { get; set; }
        public Case Case { get; set; }
        public string Data { get; set; }
    }

    public class FailedStatement
    {
        public int Id { get; set; }
        public int CaseId { get; set; }
        public Case Case { get; set; }
        public string Data { get; set; }
    }

    public class FinanceVoucher
    {
        public int Id { get; set; }
        public string CompanyName { get; set; }
        public DateTime Date { get; set; }
        public DateTime EffectiveDate { get; set; }
        public string BillReference { get; set; }
        public string DrLedger { get; set; }
        public string CrLedger { get; set; }
        public double Amount { get; set; }
        public string VoucherType { get; set; }
        public string Narration { get; set; }
        public string Status { get; set; }
        public string TransactionId { get; set; }
    }

    public class OpportunityToEarn
    {
        public int Id { get; set; }
        public int CaseId { get; set; }
        public Case Case { get; set; }
        public double HomeLoanValue { get; set; }
        public double LoanAgainstProperty { get; set; }
        public double BusinessLoan { get; set; }
        public double TermPlan { get; set; }
        public double GeneralInsurance { get; set; }
    }

    public class Statement
    {
        public int Id { get; set; }
        public int CaseId { get; set; }
        public Case Case { get; set; }
        public string AccountNumber { get; set; }
        public string CustomerName { get; set; }
        public string IfscCode { get; set; }
        public string BankName { get; set; }
        public string FilePath { get; set; } = "downloads";
        public DateTime CreatedAt { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Password { get; set; }

        public ICollection<Transaction> Transactions { get; set; }
        public ICollection<TallyVoucher> TallyVouchers { get; set; }
    }

    public class Summary
    {
        public int Id { get; set; }
        public int CaseId { get; set; }
        public Case Case { get; set; }
        public string Data { get; set; }
    }

    public class TallyVoucher
    {
        public int Id { get; set; }
        public int TransactionId { get; set; }
        public Transaction Transaction { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public string BillReference { get; set; }
        public string FailedReason { get; set; }
        public string BankLedger { get; set; }
        public bool Result { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class Transaction
    {
        public int Id { get; set; }
        public int StatementId { get; set; }
        public Statement Statement { get; set; }
        public DateTime Date { get; set; }
        public string Description { get; set; }
        public double Amount { get; set; }
        public string Category { get; set; }
        public string Type { get; set; }
        public double Balance { get; set; }
        public string Bank { get; set; } = "unknown";
        public string Entity { get; set; } = "unknown";
        public string VoucherType { get; set; } = "unknown";
        public DateTime CreatedAt { get; set; }
    }
}
