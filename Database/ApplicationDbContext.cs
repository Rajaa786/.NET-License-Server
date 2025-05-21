using Microsoft.EntityFrameworkCore;
using MyLanService.Core;

namespace MyLanService.Database
{
    public class ApplicationDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }
        public DbSet<Case> Cases { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Eod> Eods { get; set; }
        public DbSet<FailedStatement> FailedStatements { get; set; }
        public DbSet<FinanceVoucher> FinanceVouchers { get; set; }
        public DbSet<OpportunityToEarn> Opportunities { get; set; }
        public DbSet<Statement> Statements { get; set; }
        public DbSet<Summary> Summaries { get; set; }
        public DbSet<TallyVoucher> TallyVouchers { get; set; }
        public DbSet<Transaction> Transactions { get; set; }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // users
            builder.Entity<User>(eb =>
            {
                eb.ToTable("users");
                eb.HasKey(u => u.Id);
                eb.Property(u => u.Id).HasColumnName("id");
                eb.Property(u => u.Name).HasColumnName("name").IsRequired();
                eb.Property(u => u.Email).HasColumnName("email").IsRequired();
                eb.Property(u => u.Role).HasColumnName("role").HasDefaultValue("CA");
                eb.Property(u => u.Password).HasColumnName("password").IsRequired();
                eb.Property(u => u.DateJoined)
                    .HasColumnName("date_joined")
                    .HasColumnType("timestamp with time zone");
                eb.Property(u => u.ExpiryDate)
                    .HasColumnName("expiry")
                    .HasColumnType("timestamp with time zone");
                eb.Property(u => u.LastLogin)
                    .HasColumnName("last_login")
                    .HasColumnType("timestamp with time zone");
                eb.HasMany(u => u.Cases)
                    .WithOne(c => c.User)
                    .HasForeignKey(c => c.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // cases
            builder.Entity<Case>(eb =>
            {
                eb.ToTable("cases");
                eb.HasKey(c => c.Id);
                eb.Property(c => c.Id).HasColumnName("id");
                eb.Property(c => c.Name).HasColumnName("name").IsRequired();
                eb.Property(c => c.UserId).HasColumnName("user_id").IsRequired();
                eb.Property(c => c.Status).HasColumnName("status").IsRequired();
                eb.Property(c => c.Pages).HasColumnName("pages").HasDefaultValue(0);
                eb.Property(c => c.CreatedAt)
                    .HasColumnName("created_at")
                    .HasColumnType("timestamp with time zone");
                eb.Property(c => c.Deleted).HasColumnName("deleted").HasDefaultValue(false);
                eb.HasMany(c => c.Eods)
                    .WithOne(e => e.Case)
                    .HasForeignKey(e => e.CaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                eb.HasMany(c => c.FailedStatements)
                    .WithOne(f => f.Case)
                    .HasForeignKey(f => f.CaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                eb.HasMany(c => c.Opportunities)
                    .WithOne(o => o.Case)
                    .HasForeignKey(o => o.CaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                eb.HasMany(c => c.Statements)
                    .WithOne(s => s.Case)
                    .HasForeignKey(s => s.CaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                eb.HasMany(c => c.Summaries)
                    .WithOne(su => su.Case)
                    .HasForeignKey(su => su.CaseId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // categories
            builder.Entity<Category>(eb =>
            {
                eb.ToTable("categories");
                eb.HasKey(cat => cat.Id);
                eb.Property(cat => cat.Id).HasColumnName("id");
                eb.Property(cat => cat.Name).HasColumnName("category").IsRequired();
            });

            // eod
            builder.Entity<Eod>(eb =>
            {
                eb.ToTable("eod");
                eb.HasKey(e => e.Id);
                eb.Property(e => e.Id).HasColumnName("id");
                eb.Property(e => e.CaseId).HasColumnName("case_id").IsRequired();
                eb.Property(e => e.Data).HasColumnName("data").IsRequired();
            });

            // failed_statements
            builder.Entity<FailedStatement>(eb =>
            {
                eb.ToTable("failed_statements");
                eb.HasKey(f => f.Id);
                eb.Property(f => f.Id).HasColumnName("id");
                eb.Property(f => f.CaseId).HasColumnName("case_id").IsRequired();
                eb.Property(f => f.Data).HasColumnName("data").IsRequired();
            });

            // finance_vouchers
            builder.Entity<FinanceVoucher>(eb =>
            {
                eb.ToTable("finance_vouchers");
                eb.HasKey(v => v.Id);
                eb.Property(v => v.Id).HasColumnName("id");
                eb.Property(v => v.CompanyName).HasColumnName("company_name").IsRequired();
                eb.Property(v => v.Date)
                    .HasColumnName("date")
                    .HasColumnType("timestamp with time zone")
                    .IsRequired();
                eb.Property(v => v.EffectiveDate)
                    .HasColumnName("effective_date")
                    .HasColumnType("timestamp with time zone")
                    .IsRequired();
                eb.Property(v => v.BillReference).HasColumnName("bill_reference").IsRequired();
                eb.Property(v => v.DrLedger).HasColumnName("dr_ledger").IsRequired();
                eb.Property(v => v.CrLedger).HasColumnName("cr_ledger").IsRequired();
                eb.Property(v => v.Amount).HasColumnName("amount").IsRequired();
                eb.Property(v => v.VoucherType).HasColumnName("voucher_type").IsRequired();
                eb.Property(v => v.Narration).HasColumnName("narration");
                eb.Property(v => v.Status).HasColumnName("status").IsRequired();
                eb.Property(v => v.TransactionId).HasColumnName("transaction_id").IsRequired();
            });

            // opportunity_to_earn
            builder.Entity<OpportunityToEarn>(eb =>
            {
                eb.ToTable("opportunity_to_earn");
                eb.HasKey(o => o.Id);
                eb.Property(o => o.Id).HasColumnName("id");
                eb.Property(o => o.CaseId).HasColumnName("case_id").IsRequired();
                eb.Property(o => o.HomeLoanValue).HasColumnName("home_loan_value").IsRequired();
                eb.Property(o => o.LoanAgainstProperty)
                    .HasColumnName("loan_against_property")
                    .IsRequired();
                eb.Property(o => o.BusinessLoan).HasColumnName("business_loan").IsRequired();
                eb.Property(o => o.TermPlan).HasColumnName("term_plan").IsRequired();
                eb.Property(o => o.GeneralInsurance)
                    .HasColumnName("general_insurance")
                    .IsRequired();
            });

            // statements
            builder.Entity<Statement>(eb =>
            {
                eb.ToTable("statements");
                eb.HasKey(s => s.Id);
                eb.Property(s => s.Id).HasColumnName("id");
                eb.Property(s => s.CaseId).HasColumnName("case_id").IsRequired();
                eb.Property(s => s.AccountNumber).HasColumnName("account_number").IsRequired();
                eb.Property(s => s.CustomerName).HasColumnName("customer_name").IsRequired();
                eb.Property(s => s.IfscCode).HasColumnName("ifsc_code");
                eb.Property(s => s.BankName).HasColumnName("bank_name");
                eb.Property(s => s.FilePath)
                    .HasColumnName("file_path")
                    .HasDefaultValue("downloads");
                eb.Property(s => s.CreatedAt)
                    .HasColumnName("created_at")
                    .HasColumnType("timestamp with time zone");
                eb.Property(s => s.StartDate).HasColumnName("start_date");
                eb.Property(s => s.EndDate).HasColumnName("end_date");
                eb.Property(s => s.Password).HasColumnName("password");
            });

            // summary
            builder.Entity<Summary>(eb =>
            {
                eb.ToTable("summary");
                eb.HasKey(su => su.Id);
                eb.Property(su => su.Id).HasColumnName("id");
                eb.Property(su => su.CaseId).HasColumnName("case_id").IsRequired();
                eb.Property(su => su.Data).HasColumnName("data").IsRequired();
            });

            // transactions
            builder.Entity<Transaction>(eb =>
            {
                eb.ToTable("transactions");
                eb.HasKey(t => t.Id);
                eb.Property(t => t.Id).HasColumnName("id");
                eb.Property(t => t.StatementId).HasColumnName("statement_id").IsRequired();
                eb.Property(t => t.Date)
                    .HasColumnName("date")
                    .HasColumnType("timestamp with time zone")
                    .IsRequired();
                eb.Property(t => t.Description).HasColumnName("description").IsRequired();
                eb.Property(t => t.Amount).HasColumnName("amount").IsRequired();
                eb.Property(t => t.Category).HasColumnName("category").IsRequired();
                eb.Property(t => t.Type).HasColumnName("type").IsRequired();
                eb.Property(t => t.Balance).HasColumnName("balance").IsRequired();
                eb.Property(t => t.Bank).HasColumnName("bank").HasDefaultValue("unknown");
                eb.Property(t => t.Entity).HasColumnName("entity").HasDefaultValue("unknown");
                eb.Property(t => t.VoucherType)
                    .HasColumnName("voucher_type")
                    .HasDefaultValue("unknown");
                eb.Property(t => t.CreatedAt)
                    .HasColumnName("created_at")
                    .HasColumnType("timestamp with time zone")
                    .IsRequired();
            });

            // tally_voucher
            builder.Entity<TallyVoucher>(eb =>
            {
                eb.ToTable("tally_voucher");
                eb.HasKey(tv => tv.Id);
                eb.Property(tv => tv.Id).HasColumnName("id");
                eb.Property(tv => tv.TransactionId).HasColumnName("transaction_id").IsRequired();
                eb.Property(tv => tv.EffectiveDate)
                    .HasColumnName("effective_date")
                    .HasColumnType("timestamp with time zone");
                eb.Property(tv => tv.BillReference).HasColumnName("bill_reference");
                eb.Property(tv => tv.FailedReason).HasColumnName("failed_reason");
                eb.Property(tv => tv.BankLedger).HasColumnName("bank_ledger").IsRequired();
                eb.Property(tv => tv.Result).HasColumnName("result").IsRequired();
                eb.Property(tv => tv.CreatedAt)
                    .HasColumnName("created_at")
                    .HasColumnType("timestamp with time zone")
                    .IsRequired();
            });
        }
    }
}
