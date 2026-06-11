using System.IO;
using DailyVertViewer.Wpf.Infrastructure;
using DailyVertViewer.Wpf.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Newtonsoft.Json.Linq;

namespace DailyVertViewer.Wpf.Services;

public sealed class GoogleCalendarService
{
    private static readonly string[] Scopes = [CalendarService.Scope.CalendarReadonly];
    private readonly TimeZoneInfo _jst;

    public GoogleCalendarService(TimeZoneInfo jst)
    {
        _jst = jst;
    }

    public async Task<IReadOnlyList<CalendarEventItem>> FetchTodayEventsAsync(CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken);
        var calendarColors = await GetCalendarColorsAsync(service, cancellationToken);
        var response = await service.CalendarList.List().ExecuteAsync(cancellationToken);

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _jst);
        var rangeStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).ToUniversalTime();
        var rangeEnd = new DateTimeOffset(now.Year, now.Month, now.Day, 23, 59, 59, now.Offset).ToUniversalTime();

        var result = new List<CalendarEventItem>();
        foreach (var calendar in response.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(calendar.Id))
            {
                continue;
            }

            var color = calendarColors.TryGetValue(calendar.Id, out var value) ? value : "#a2d5f2";
            try
            {
                var request = service.Events.List(calendar.Id);
                request.TimeMinDateTimeOffset = rangeStart;
                request.TimeMaxDateTimeOffset = rangeEnd;
                request.SingleEvents = true;
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

                Events events = await request.ExecuteAsync(cancellationToken);
                foreach (var item in events.Items ?? [])
                {
                    var isAllDay = item.Start.DateTimeDateTimeOffset is null;
                    var start = item.Start.DateTimeDateTimeOffset
                        ?? DateTimeOffset.Parse(item.Start.Date!);
                    var end = item.End.DateTimeDateTimeOffset
                        ?? DateTimeOffset.Parse(item.End.Date!);

                    result.Add(new CalendarEventItem
                    {
                        Summary = item.Summary ?? "No Title",
                        StartTime = TimeZoneInfo.ConvertTime(start, _jst),
                        EndTime = TimeZoneInfo.ConvertTime(end, _jst),
                        ColorHex = color,
                        IsAllDay = isAllDay
                    });
                }
            }
            catch
            {
            }
        }

        return result;
    }

    private async Task<CalendarService> CreateServiceAsync(CancellationToken cancellationToken)
    {
        var credentialsPath = PathHelper.FindUpward("credentials.json")
            ?? throw new FileNotFoundException("credentials.json was not found.");
        var tokenPath = PathHelper.FindUpward("token.json");

        UserCredential credential;
        if (!string.IsNullOrWhiteSpace(tokenPath) && File.Exists(tokenPath))
        {
            credential = await CreateCredentialFromTokenJsonAsync(credentialsPath, tokenPath, cancellationToken);
        }
        else
        {
            using var stream = File.OpenRead(credentialsPath);
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes,
                "user",
                cancellationToken,
                new FileDataStore(Path.Combine(AppContext.BaseDirectory, "google-token-store"), true));
        }

        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "dailyVertViewer"
        });
    }

    private static async Task<UserCredential> CreateCredentialFromTokenJsonAsync(
        string credentialsPath,
        string tokenPath,
        CancellationToken cancellationToken)
    {
        var secrets = GoogleClientSecrets.FromFile(credentialsPath).Secrets;
        var tokenJson = await File.ReadAllTextAsync(tokenPath, cancellationToken);
        var tokenObject = JObject.Parse(tokenJson);

        var token = new TokenResponse
        {
            AccessToken = tokenObject.Value<string>("access_token"),
            RefreshToken = tokenObject.Value<string>("refresh_token"),
            TokenType = tokenObject.Value<string>("token_type") ?? "Bearer",
            Scope = tokenObject["scopes"]?.Type == JTokenType.Array
                ? string.Join(' ', tokenObject["scopes"]!.Values<string>())
                : tokenObject.Value<string>("scope")
        };

        if (DateTime.TryParse(tokenObject.Value<string>("expiry"), out var expiry))
        {
            token.IssuedUtc = expiry.ToUniversalTime().AddHours(-1);
        }

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = Scopes
        });

        var credential = new UserCredential(flow, "user", token);
        if (credential.Token.IsStale)
        {
            await credential.RefreshTokenAsync(cancellationToken);
            var issuedUtc = credential.Token.IssuedUtc == default
                ? DateTime.UtcNow
                : credential.Token.IssuedUtc;
            var expiryOut = issuedUtc.AddSeconds(credential.Token.ExpiresInSeconds ?? 3600)
                .ToUniversalTime()
                .ToString("O");
            var refreshed = new JObject
            {
                ["access_token"] = credential.Token.AccessToken,
                ["refresh_token"] = credential.Token.RefreshToken ?? token.RefreshToken,
                ["token_type"] = credential.Token.TokenType ?? "Bearer",
                ["scope"] = credential.Token.Scope,
                ["expiry"] = expiryOut
            };
            await File.WriteAllTextAsync(tokenPath, refreshed.ToString(), cancellationToken);
        }

        return credential;
    }

    private static async Task<Dictionary<string, string>> GetCalendarColorsAsync(
        CalendarService service,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>();
        var response = await service.CalendarList.List().ExecuteAsync(cancellationToken);
        foreach (var calendar in response.Items ?? [])
        {
            if (!string.IsNullOrWhiteSpace(calendar.Id))
            {
                result[calendar.Id] = string.IsNullOrWhiteSpace(calendar.BackgroundColor)
                    ? "#a2d5f2"
                    : calendar.BackgroundColor;
            }
        }

        return result;
    }
}
