using dotenv.net;
using IGDB;
using System.Text.RegularExpressions;
using WikiClientLibrary;
using WikiClientLibrary.Client;
using WikiClientLibrary.Generators;
using WikiClientLibrary.Pages;
using WikiClientLibrary.Pages.Queries.Properties;
using WikiClientLibrary.Sites;

class Program
{
    public static IGDBClient IgdbClient { get; private set; }
    public static bool PromptForIgdbOnTalkPage = true;

    static void Main(string[] args)
    {
        DotEnv.Load(new DotEnvOptions(probeForEnv: true, probeLevelsToSearch: 6));

        IgdbClient = IGDBClient.CreateWithDefaults(
            Environment.GetEnvironmentVariable("igdb_client_id")!,
            Environment.GetEnvironmentVariable("igdb_client_secret")!);

        MainAsync().Wait();
    }

    static async Task MainAsync()
    {
        using var client = new WikiClient
        {
            ClientUserAgent = "APWikiBot/1.0 (Silasary)"
        };
        var site = new WikiSite(client, "https://archipelago.miraheze.org/w/api.php");
        await site.Initialization;

        try
        {
            await site.LoginAsync(Environment.GetEnvironmentVariable("botuser")!, Environment.GetEnvironmentVariable("botpass")!);
        }
        catch (WikiClientException ex)
        {
            Console.WriteLine(ex.Message);
            return;
        }

        Console.WriteLine(site.SiteInfo.SiteName);
        Console.WriteLine($"Connected as {site.AccountInfo}");
        
        var myUserPage = new WikiPage(site, "User:" + site.AccountInfo.Name);
        await myUserPage.RefreshAsync(PageQueryOptions.FetchContent);
        var env = GetEnv(myUserPage);
        if (!bool.TryParse(env["Active"], out var active) || !active)
        {
            Console.WriteLine("Disabled by UserPage");
            return;
        }
        _ = bool.TryParse(env["RearrangeTemplates"], out bool RearrangeTemplates);
        _ = bool.TryParse(env["UploadBoxArt"], out bool UploadBoxArt);
        Program.PromptForIgdbOnTalkPage = bool.TryParse(env["PromptForIGDBOnTalkPage"], out bool prompt) && prompt;
        _ = bool.TryParse(env["CheckSupportedNavbox"], out bool CheckSupportedNavbox);
        _ = bool.TryParse(env["CheckFranchiseNavbox"], out bool CheckFranchiseNavbox);

        //var page = new WikiPage(site, "User:Silasary/sandbox");
        //await GamePageChecks.CheckTemplates(page);

        var games = new WikiPage(site, "Category:Games");
        await games.RefreshAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects);
        var catInfo = games.GetPropertyGroup<CategoryInfoPropertyGroup>();
        Console.WriteLine("Category '{0}' has {1} members.", games.Title, catInfo.MembersCount);
        var members = new CategoryMembersGenerator(site, "Category:Games");
        await foreach (var member in members.EnumPagesAsync(PageQueryOptions.FetchContent | PageQueryOptions.ResolveRedirects))
        {
            if (member.NamespaceId != 0)
            {
                Console.WriteLine($"Skipping {member.Title} (not in valid namespace)");
                continue;
            }
            //Console.WriteLine(" - {0}", member.Title);
            if (RearrangeTemplates)
                await GamePageChecks.CheckTemplates(member);
            if (UploadBoxArt)
                await GamePageChecks.CheckForBoxArt(member);
            if (CheckSupportedNavbox)
                await GamePageChecks.CheckSupportedNavbox(member);
            if (CheckFranchiseNavbox)
                await GamePageChecks.CheckFranchiseNavbox(member);
        }

        // We're done here
        await site.LogoutAsync();
    }

    private static Dictionary<string, string> GetEnv(WikiPage myUserPage)
    {
        Regex tableEntry = new("^\\| ([A-Za-z]+) \\|\\| ([A-Za-z]+)", RegexOptions.Multiline);
        var matches = tableEntry.Matches(myUserPage.Content);
        var env = new Dictionary<string, string>();
        foreach (Match match in matches)
        {
            env[match.Groups[1].Value] = match.Groups[2].Value;
            Console.WriteLine($"> {match.Groups[1].Value} = {match.Groups[2].Value}");
        }
        return env;
    }
}
