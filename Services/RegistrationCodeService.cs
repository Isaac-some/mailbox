using System.Security.Cryptography;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services;

public sealed class RegistrationCodeService : IRegistrationCodeService
{
    private readonly MailArchiverDbContext _context;
    private readonly IUserService _userService;
    private readonly ILogger<RegistrationCodeService> _logger;

    public RegistrationCodeService(
        MailArchiverDbContext context,
        IUserService userService,
        ILogger<RegistrationCodeService> logger)
    {
        _context = context;
        _userService = userService;
        _logger = logger;
    }

    public async Task<string> CreateAsync(int createdByUserId, int validDays)
    {
        validDays = Math.Clamp(validDays, 1, 30);
        var code = CreatePlaintextCode();
        var now = DateTime.UtcNow;

        _context.RegistrationCodes.Add(new RegistrationCode
        {
            CodeHash = HashCode(code),
            CodePrefix = code[..Math.Min(code.Length, 10)],
            CreatedAt = now,
            ExpiresAt = now.AddDays(validDays),
            CreatedByUserId = createdByUserId
        });
        await _context.SaveChangesAsync();

        return code;
    }

    public async Task<IReadOnlyList<RegistrationCodeViewModel>> GetAllAsync()
    {
        return await (
            from code in _context.RegistrationCodes.AsNoTracking()
            join user in _context.Users.AsNoTracking()
                on code.UsedByUserId equals user.Id into usedByUsers
            from usedBy in usedByUsers.DefaultIfEmpty()
            orderby code.CreatedAt descending
            select new RegistrationCodeViewModel
            {
                Id = code.Id,
                CodePrefix = code.CodePrefix,
                CreatedAt = code.CreatedAt,
                ExpiresAt = code.ExpiresAt,
                UsedAt = code.UsedAt,
                RevokedAt = code.RevokedAt,
                UsedByUsername = usedBy == null ? null : usedBy.Username
            }).ToListAsync();
    }

    public async Task<bool> RevokeAsync(int id)
    {
        var now = DateTime.UtcNow;
        var affected = await _context.RegistrationCodes
            .Where(code => code.Id == id && code.UsedAt == null && code.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.RevokedAt, now));
        return affected == 1;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string authorizationCode,
        string username,
        string email,
        string password)
    {
        username = username.Trim();
        email = email.Trim().ToLowerInvariant();
        var codeHash = HashCode(authorizationCode);
        var now = DateTime.UtcNow;

        if (await _context.Users.AnyAsync(user =>
                user.Username.ToLower() == username.ToLower() || user.Email.ToLower() == email))
        {
            return RegistrationResult.Failure("用户名或邮箱已被使用");
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var claimed = await _context.RegistrationCodes
                .Where(code =>
                    code.CodeHash == codeHash &&
                    code.UsedAt == null &&
                    code.RevokedAt == null &&
                    code.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.UsedAt, now));

            if (claimed != 1)
            {
                await transaction.RollbackAsync();
                return RegistrationResult.Failure("授权码无效、已使用或已过期");
            }

            var user = new User
            {
                Username = username,
                Email = email,
                PasswordHash = _userService.HashPassword(password),
                IsAdmin = false,
                IsSelfManager = true,
                IsActive = true,
                CreatedAt = now
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            var code = await _context.RegistrationCodes
                .SingleAsync(item => item.CodeHash == codeHash);
            code.UsedByUserId = user.Id;
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "User {UserId} registered with one-time registration code {CodePrefix}",
                user.Id,
                code.CodePrefix);
            return RegistrationResult.Success(user);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync();
            _logger.LogWarning(ex, "Registration failed because the username or email conflicted");
            return RegistrationResult.Failure("用户名或邮箱已被使用");
        }
    }

    private static string CreatePlaintextCode()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();
        return $"KZ-{token}";
    }

    private static string HashCode(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
