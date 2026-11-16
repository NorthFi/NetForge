// root/selfhost/src/NetForge.Api/Core.cs
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace NetForge.Api;

public static class Store
{
    public static async Task Initialize(NpgsqlDataSource pool, IConfiguration settings)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "db");
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Database migrations directory missing");
        var migrations = Directory.EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (migrations.Length == 0) throw new InvalidOperationException("Database migrations missing");
        await using var conn = await pool.OpenConnectionAsync();
        // These numbered scripts contain idempotent DDL; never skip new upgrades
        // merely because an initial bootstrap already occurred.
        foreach (var migration in migrations)
        {
            await using var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(migration), conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var username = (settings["NETFORGE_ADMIN_USER"] ?? "").Trim();
        var password = settings["NETFORGE_ADMIN_PASSWORD"] ?? "";
        if (username.Length is < 3 or > 80 || password.Length < 14 ||
            password.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Set unique NETFORGE_ADMIN_USER and NETFORGE_ADMIN_PASSWORD (14+ characters)");

        await using var query = new NpgsqlCommand("SELECT count(*) FROM app_users", conn);
        var count = Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO app_users(username,password_hash,role) VALUES (@user,@hash,'admin')", conn);
            insert.Parameters.AddWithValue("user", username);
            insert.Parameters.AddWithValue("hash", Passwords.Hash(password));
            await insert.ExecuteNonQueryAsync();
        }
    }

    public static NpgsqlParameter P(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    public static async Task<List<Dictionary<string, object?>>> Rows(
        NpgsqlDataSource pool, string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = await pool.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        return await Read(cmd);
    }

    public static async Task<List<Dictionary<string, object?>>> Read(NpgsqlCommand cmd)
    {
        var results = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = value;
            }
            results.Add(row);
        }
        return results;
    }

    public static async Task<int> Execute(
        NpgsqlDataSource pool, string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = await pool.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<Guid> Insert(
        NpgsqlDataSource pool, string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = await pool.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        var value = await cmd.ExecuteScalarAsync();
        if (value is not Guid guid) throw new InvalidOperationException("INSERT did not return an ID");
        return guid;
    }

    public static async Task Audit(NpgsqlDataSource pool, string username, string action,
        string entity, string id)
    {
        await Execute(pool, "INSERT INTO audit_log(username,action,entity,entity_id) VALUES (@u,@a,@e,@id)",
            P("u", username), P("a", action), P("e", entity), P("id", id));
    }
}

public static class Passwords
{
    private const int Iterations = 310_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "v1" ||
            !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 2_000_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}

public sealed record LoginInput(string Username, string Password);
public sealed record StaffInput(string Username, string Password, string Role);
public sealed record BranchInput(string Name, string Address);
public sealed record SiteInput(string Number, string Name, string Address, Guid? BranchId,
    double? Latitude, double? Longitude);
public sealed record SectorInput(Guid HighsiteId, string Name, string Ssid, decimal? Azimuth,
    decimal? FrequencyMhz, int? MaxCustomers);
public sealed record CustomerInput(string Account, string FirstName, string Surname, string Email,
    string Phone, string Address, string Status, Guid? BranchId, Guid? HighsiteId, Guid? SectorId);
public sealed record PackageInput(string Name, int DownloadMbps, int UploadMbps,
    decimal MonthlyPrice, string MikrotikRateLimit);
public sealed record ServiceInput(Guid CustomerId, Guid PackageId, string Username,
    string State, string? StaticIpv4);
public sealed record CredentialInput(string Password);
public sealed record NasInput(string Address, string Name, string Secret, string Description);
public sealed record TicketInput(Guid? CustomerId, Guid? HighsiteId, string Subject,
    string Description, string Type, string Priority);
public sealed record TicketStatusInput(string Status);
public sealed record InvoiceInput(Guid CustomerId, string Number, decimal Total, DateOnly? DueDate);
public sealed record PaymentInput(Guid InvoiceId, decimal Amount, string Reference);
public sealed record EquipmentInput(Guid? CustomerId, Guid? HighsiteId, string Category,
    string Name, string Model, string ManagementIp, string SerialNumber, string Ownership);
