using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Security;

// Local console only. No HTTP enrollment or password reset endpoint, no implicit migrations.
public static class OperatorProvisioning
{
    public static async Task RunAsync(IServiceProvider services, string[] args)
    {
        string Value(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new InvalidOperationException($"Required: {flag}");
        }
        var output = Path.GetFullPath(Value("--output"));
        if (File.Exists(output)) throw new InvalidOperationException("Output already exists; refusing credential replacement.");
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use a local interactive console.");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProspectionCrmDbContext>();
        Guid user; Guid workspace;
        if (args.Contains("--bootstrap-empty"))
        {
            if (await db.UserAccounts.AnyAsync() || await db.Workspaces.AnyAsync())
                throw new InvalidOperationException("Installation is not empty; supply explicit existing IDs.");
            var result = await scope.ServiceProvider.GetRequiredService<IBootstrapService>().BootstrapAsync(default);
            if (result.Error is not null || result.Bootstrap is null || !result.Created) throw new InvalidOperationException("Bootstrap refused.");
            user = result.Bootstrap.OwnerUserId; workspace = result.Bootstrap.WorkspaceId;
        }
        else
        {
            user = Guid.Parse(Value("--user-id")); workspace = Guid.Parse(Value("--workspace-id"));
        }
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspace && w.OwnerUserId == user && w.ArchivedAt == null
            && db.UserAccounts.Any(u => u.Id == user))) throw new InvalidOperationException("Owner/workspace binding refused.");
        Console.WriteLine($"Provision operator {user:D}, workspace {workspace:D}.");
        Console.Write("Password (12–256 characters): "); var password = ReadPassword();
        Console.Write("Confirm password: "); var confirmation = ReadPassword();
        if (password.Length is < 12 or > 256 || password != confirmation) throw new InvalidOperationException("Passwords rejected.");
        var options = new OperatorOptions { UserId = user, WorkspaceId = workspace };
        options.PasswordHash = new PasswordHasher<OperatorOptions>().HashPassword(options, password);
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, new Dictionary<string, string>
        {
            ["Operator:UserId"] = user.ToString("D"), ["Operator:WorkspaceId"] = workspace.ToString("D"),
            ["Operator:PasswordHash"] = options.PasswordHash
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Credential file created. Import into server User Secrets; protect and remove the temporary file.");
    }
    private static string ReadPassword()
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar))
            { if (text.Length >= 256) throw new InvalidOperationException("Password too long."); text.Append(key.KeyChar); }
        }
    }
}
