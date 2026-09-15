using System.Security.Cryptography;
using System.Text.Json;

namespace BMX.Services;

// Local operator accounts in users.json next to the app. PBKDF2 hashes, no
// external identity provider: the display has to work with no network at all.
public sealed class UserStore
{
    private sealed record UserRecord(string Username, string Salt, string Hash, string Role);

    private readonly string _path;
    private readonly ILogger<UserStore> _log;
    private readonly object _lock = new();
    private List<UserRecord> _users = new();

    private const int Iterations = 100_000;

    public UserStore(IHostEnvironment env, ILogger<UserStore> log)
    {
        _log = log;
        _path = Path.Combine(env.ContentRootPath, "users.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                try
                {
                    _users = JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(_path)) ?? new();
                    if (_users.Count > 0) return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("users.json unreadable ({Error}); starting with the default account", ex.Message);
                }
            }
            // First run: a single admin account so the logon page has something
            // to accept. Change it from the Settings page.
            _users = new List<UserRecord> { Make("admin", "admin", "Admin") };
            Save();
            _log.LogWarning("Created users.json with the default account admin / admin - change it");
        }
    }

    private void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static UserRecord Make(string username, string password, string role)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new UserRecord(username, Convert.ToBase64String(salt), Convert.ToBase64String(hash), role);
    }

    public bool Validate(string username, string password, out string role)
    {
        role = "";
        UserRecord? u;
        lock (_lock) u = _users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
        if (u == null) return false;
        byte[] salt = Convert.FromBase64String(u.Salt);
        byte[] expected = Convert.FromBase64String(u.Hash);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
        role = u.Role;
        return true;
    }

    public IReadOnlyList<(string Username, string Role)> List()
    {
        lock (_lock) return _users.Select(u => (u.Username, u.Role)).ToList();
    }

    public bool SetPassword(string username, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword)) return false;
        lock (_lock)
        {
            int i = _users.FindIndex(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            _users[i] = Make(_users[i].Username, newPassword, _users[i].Role);
            Save();
            return true;
        }
    }
}
