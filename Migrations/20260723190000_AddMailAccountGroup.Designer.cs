using MailArchiver.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations;

[DbContext(typeof(MailArchiverDbContext))]
[Migration("20260723190000_AddMailAccountGroup")]
partial class AddMailAccountGroup
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasDefaultSchema("mail_archiver")
            .HasAnnotation("ProductVersion", "10.0.9")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);
#pragma warning restore 612, 618
    }
}
