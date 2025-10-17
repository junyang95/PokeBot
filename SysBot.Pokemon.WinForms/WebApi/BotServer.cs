using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using SysBot.Base;
using SysBot.Pokemon.Discord;
using SysBot.Pokemon.Helpers;
using SysBot.Pokemon.WinForms.WebApi.Models;
using static SysBot.Pokemon.WinForms.WebApi.RestartManager;

namespace SysBot.Pokemon.WinForms.WebApi;

public partial class BotServer(Main mainForm, int port = 8080, int tcpPort = 8081) : IDisposable
{
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private readonly int _port = port;
    private readonly int _tcpPort = tcpPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Main _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
    private volatile bool _running;
    private string? _htmlTemplate;

    // Whitelist of allowed method names for security
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "SendAll"
    };

    // JSON serialization options
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Cached JsonSerializer options for security contexts
    private static class CachedJsonOptions
    {
        public static readonly JsonSerializerOptions Secure = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 10,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }



    [System.Text.RegularExpressions.GeneratedRegex(@"[<>""'&;\/]")]
    private static partial System.Text.RegularExpressions.Regex CleanupRegex();

    private string HtmlTemplate
    {
        get
        {
            _htmlTemplate ??= LoadEmbeddedResource("BotControlPanel.html");
            return _htmlTemplate;
        }
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(fullResourceName))
        {
            throw new FileNotFoundException($"Embedded resource '{resourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        using var stream = assembly.GetManifestResourceStream(fullResourceName) ?? throw new FileNotFoundException($"Could not load embedded resource '{fullResourceName}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] LoadEmbeddedResourceBinary(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(fullResourceName))
        {
            throw new FileNotFoundException($"Embedded resource '{resourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        using var stream = assembly.GetManifestResourceStream(fullResourceName) ?? throw new FileNotFoundException($"Could not load embedded resource '{fullResourceName}'");
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public void Start()
    {
        if (_running) return;

        try
        {
            _listener = new HttpListener();

            try
            {
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
                LogUtil.LogInfo($"Web server listening on all interfaces at port {_port}", "WebServer");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();

                LogUtil.LogError($"Web server requires administrator privileges for network access. Currently limited to localhost only.", "WebServer");
                LogUtil.LogInfo("To enable network access, either:", "WebServer");
                LogUtil.LogInfo("1. Run this application as Administrator", "WebServer");
                LogUtil.LogInfo($"2. Or run this command as admin: netsh http add urlacl url=http://+:{_port}/ user=Everyone", "WebServer");
            }

            _running = true;

            _listenerThread = new Thread(Listen)
            {
                IsBackground = true,
                Name = "BotWebServer"
            };
            _listenerThread.Start();

        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to start web server: {ex.Message}", "WebServer");
            throw;
        }
    }

    public void Stop()
    {
        if (!_running) return;

        try
        {
            _running = false;
            _cts.Cancel();


            _listener?.Stop();
            _listenerThread?.Join(5000);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }

    private void Listen()
    {
        while (_running && _listener != null)
        {
            try
            {
                var asyncResult = _listener.BeginGetContext(null, null);

                while (_running && !asyncResult.AsyncWaitHandle.WaitOne(100))
                {
                }

                if (!_running)
                    break;

                var context = _listener.EndGetContext(asyncResult);

                ThreadPool.QueueUserWorkItem(async _ =>
                {
                    try
                    {
                        await HandleRequest(context);
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Error handling request: {ex.Message}", "WebServer");
                    }
                });
            }
            catch (HttpListenerException ex) when (!_running || ex.ErrorCode == 995)
            {
                break;
            }
            catch (ObjectDisposedException) when (!_running)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    LogUtil.LogError($"Error in listener: {ex.Message}", "WebServer");
                }
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            SetCorsHeaders(request, response);

            if (request.HttpMethod == "OPTIONS")
            {
                await SendResponseAsync(response, 200, "");
                return;
            }


            var (statusCode, content, contentType) = await ProcessRequestAsync(request);

            if ((contentType == "image/x-icon" || contentType == "image/png") && content is byte[] imageBytes)
            {
                await SendBinaryResponseAsync(response, 200, imageBytes, contentType);
            }
            else
            {
                var responseContent = content?.ToString() ?? "Not Found";
                if (request.Url?.LocalPath?.Contains("/bots") == true)
                {
                    LogUtil.LogInfo($"Bots API response: {responseContent[..Math.Min(200, responseContent.Length)]}", "WebAPI");
                }
                await SendResponseAsync(response, statusCode, responseContent, contentType);
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error processing request: {ex.Message}", "WebServer");
            await TrySendErrorResponseAsync(response, 500, "Internal Server Error");
        }
    }

    private static void SetCorsHeaders(HttpListenerRequest request, HttpListenerResponse response)
    {
        var origin = request.Headers["Origin"] ?? "http://localhost";
        if (IsAllowedOrigin(origin))
        {
            response.Headers.Add("Access-Control-Allow-Origin", origin);
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }
    }
    private async Task<(int statusCode, object? content, string contentType)> ProcessRequestAsync(HttpListenerRequest request)
    {
        var path = request.Url?.LocalPath ?? "";

        return path switch
        {
            "/" => (200, HtmlTemplate, "text/html"),
            "/BotControlPanel.css" => (200, LoadEmbeddedResource("BotControlPanel.css"), "text/css"),
            "/BotControlPanel.js" => (200, LoadEmbeddedResource("BotControlPanel.js"), "text/javascript"),
            "/api/bot/instances" => (200, await GetInstancesAsync(), "application/json"),
            var p when p.StartsWith("/api/bot/instances/") && p.EndsWith("/bots") =>
                (200, await Task.FromResult(GetBots(ExtractPort(p))), "application/json"),
            var p when p.StartsWith("/api/bot/instances/") && p.EndsWith("/command") =>
                (200, await RunCommand(request, ExtractPort(p)), "application/json"),
            "/api/bot/command/all" => (200, await RunAllCommandAsync(request), "application/json"),
            "/api/bot/update/check" => (200, await CheckForUpdates(), "application/json"),
            "/api/bot/update/idle-status" => (200, await GetIdleStatusAsync(), "application/json"),
            "/api/bot/update/all" => (200, await UpdateAllInstances(request), "application/json"),
            "/api/bot/update/active" => (200, await Task.FromResult(GetActiveUpdates()), "application/json"),
            "/api/bot/update/clear" => (200, await Task.FromResult(ClearUpdateSession()), "application/json"),
            var p when p.StartsWith("/api/bot/instances/") && p.EndsWith("/update") && request.HttpMethod == "POST" =>
                (200, await UpdateSingleInstance(request, ExtractPort(p)), "application/json"),
            "/api/bot/restart/all" => (200, await RestartAllInstances(request), "application/json"),
            "/api/bot/restart/schedule" => (200, await UpdateRestartSchedule(request), "application/json"),
            var p when p.StartsWith("/api/bot/instances/") && p.EndsWith("/remote/button") =>
                (200, await HandleRemoteButton(request, ExtractPort(p)), "application/json"),
            var p when p.StartsWith("/api/bot/instances/") && p.EndsWith("/remote/macro") =>
                (200, await HandleRemoteMacro(request, ExtractPort(p)), "application/json"),
            "/icon.ico" => (200, GetIconBytes(), "image/x-icon"),
            "/LeftJoyCon.png" => (200, LoadEmbeddedResourceBinary("LeftJoyCon.png"), "image/png"),
            "/RightJoyCon.png" => (200, LoadEmbeddedResourceBinary("RightJoyCon.png"), "image/png"),
            "/api/trade/code" => (200, await GenerateTradeCode(request), "application/json"),
            _ => (404, null, "text/plain")
        };
    }

    // 放在同一个类里或单独的 static helper 类里
    private static TResult WithInfo<TResult>(
        IPokeBotRunner runner,
        Func<TradeQueueInfo<PK9>, TResult> sv,
        Func<TradeQueueInfo<PK8>, TResult> swsh,
        Func<TradeQueueInfo<PB8>, TResult> bdsp,
        Func<TradeQueueInfo<PA8>, TResult> la,
        Func<TradeQueueInfo<PB7>, TResult> lgpe)
    {
        return runner switch
        {
            PokeBotRunner<PK9> r => sv(r.Hub.Queues.Info),
            PokeBotRunner<PK8> r => swsh(r.Hub.Queues.Info),
            PokeBotRunner<PB8> r => bdsp(r.Hub.Queues.Info),
            PokeBotRunner<PA8> r => la(r.Hub.Queues.Info),
            PokeBotRunner<PB7> r => lgpe(r.Hub.Queues.Info),
            _ => throw new NotSupportedException($"Unsupported runner type: {runner?.GetType().Name}")
        };
    }

    private static TResult WithHub<TResult>(
        IPokeBotRunner runner,
        Func< PokeTradeHub<PK9>, TResult> sv,
        Func< PokeTradeHub<PK8>, TResult> swsh,
        Func< PokeTradeHub<PB8>, TResult> bdsp,
        Func< PokeTradeHub<PA8>, TResult> la,
        Func< PokeTradeHub<PB7>, TResult> lgpe)
    {
        return runner switch
        {
            PokeBotRunner<PK9> r => sv(r.Hub),
            PokeBotRunner<PK8> r => swsh(r.Hub),
            PokeBotRunner<PB8> r => bdsp(r.Hub),
            PokeBotRunner<PA8> r => la(r.Hub),
            PokeBotRunner<PB7> r => lgpe(r.Hub),
            _ => throw new NotSupportedException($"Unsupported runner type: {runner?.GetType().Name}")
        };
    }

    private static TResult ForGame<TResult>(
        IPokeBotRunner runner,
        Func<TResult> sv,   // PK9
        Func<TResult> swsh, // PK8
        Func<TResult> bdsp, // PB8
        Func<TResult> la,   // PA8
        Func<TResult> lgpe) // PB7
    {
        return runner switch
        {
            PokeBotRunner<PK9> => sv(),
            PokeBotRunner<PK8> => swsh(),
            PokeBotRunner<PB8> => bdsp(),
            PokeBotRunner<PA8> => la(),
            PokeBotRunner<PB7> => lgpe(),
            _ => throw new NotSupportedException($"Unsupported runner: {runner?.GetType().Name}")
        };
    }

    private static T ForGame<T>(
        IPokeBotRunner runner,
        T sv,   // PK9
        T swsh, // PK8
        T bdsp, // PB8
        T la,   // PA8
        T lgpe) // PB7
    {
        return runner switch
        {
            PokeBotRunner<PK9> => sv,
            PokeBotRunner<PK8> => swsh,
            PokeBotRunner<PB8> => bdsp,
            PokeBotRunner<PA8> => la,
            PokeBotRunner<PB7> => lgpe,
            _ => throw new NotSupportedException($"Unsupported runner: {runner?.GetType().Name}")
        };
    }

    private IPokeBotRunner RequireRunner()
        => mainForm.Runner ?? throw new InvalidOperationException("Runner not ready.");

    private ProgramConfig LoadConfig() => mainForm.Config ?? throw new InvalidOperationException("Config not ready.");
    private async Task<object?> GenerateTradeCode(HttpListenerRequest request)
    {
        var runner = RequireRunner();

        var userIDRaw = request.QueryString["userID"];
        userIDRaw = "1000";
        if (!ulong.TryParse(userIDRaw, out var userId))
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "没有登录，请先登录",
            }, JsonOptions);
        }

        bool isQueued = WithInfo(
            runner,
            sv   => sv.IsUserInQueue(userId),
            swsh => swsh.IsUserInQueue(userId),
            bdsp => bdsp.IsUserInQueue(userId),
            la   => la.IsUserInQueue(userId),
            lgpe => lgpe.IsUserInQueue(userId)
        );

        if (isQueued)
        {

            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "您已经在等待队列中了,请先完成交换",
            }, JsonOptions);
        }

        var content = request.QueryString["content"];
        content = "Iron Thorns @ Black Glasses\nAbility: Quark Drive\nLevel: 52\nShiny: Yes\nTera Type: Stellar\nEVs: 252 SpA / 252 SpD\nHardy Nature\n- Body Slam\n- Swords Dance\n- Ice Punch\n- Charge Beam\nBall: Dusk Ball\nShiny: Yes";
        content = ReusableActions.StripCodeBlock(content);

        bool isEgg = ForGame(runner,
            () => TradeExtensions<PK9>.IsEggCheck(content),
            () => TradeExtensions<PK8>.IsEggCheck(content),
            () => TradeExtensions<PB8>.IsEggCheck(content),
            () => TradeExtensions<PA8>.IsEggCheck(content),
            () => TradeExtensions<PB7>.IsEggCheck(content)
        );

        if (!ShowdownParsing.TryParseAnyLanguage(content, out ShowdownSet? set) || set == null || set.Species == 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "Showndown set不合法，请重新调整",
            }, JsonOptions);
        }

        byte finalLanguage = LanguageHelper.GetFinalLanguage(
            content, set,
            (byte)WithHub(runner,
                svHub => svHub.Config.Legality.GenerateLanguage,
                swshHub => swshHub.Config.Legality.GenerateLanguage,
                bdspHub => bdspHub.Config.Legality.GenerateLanguage,
                laHub => laHub.Config.Legality.GenerateLanguage,
                lgpeHub => lgpeHub.Config.Legality.GenerateLanguage
                ),
            ForGame(runner,
                TradeExtensions<PK9>.DetectShowdownLanguage,
                TradeExtensions<PK8>.DetectShowdownLanguage,
                TradeExtensions<PB8>.DetectShowdownLanguage,
                 TradeExtensions<PA8>.DetectShowdownLanguage,
                TradeExtensions<PB7>.DetectShowdownLanguage
                )
        );

        var template = AutoLegalityWrapper.GetTemplate(set);

        if (set.InvalidLines.Count != 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "Showndown set不合法，请重新调整",
            }, JsonOptions);
        }

        var sav = ForGame(runner,
            () => LanguageHelper.GetTrainerInfoWithLanguage<PK9>((LanguageID)finalLanguage),
            () => LanguageHelper.GetTrainerInfoWithLanguage<PK8>((LanguageID)finalLanguage),
            () => LanguageHelper.GetTrainerInfoWithLanguage<PB8>((LanguageID)finalLanguage),
            () => LanguageHelper.GetTrainerInfoWithLanguage<PA8>((LanguageID)finalLanguage),
            () => LanguageHelper.GetTrainerInfoWithLanguage<PB7>((LanguageID)finalLanguage)
        );

        PKM pkm;
        string result;

        if (isEgg)
        {
            // Use ALM's GenerateEgg method for eggs
            pkm = sav.GenerateEgg(template, out var eggResult);
            result = eggResult.ToString();
        }
        else
        {
            // Use normal generation for non-eggs
            pkm = sav.GetLegal(template, out result);
        }

        if (pkm == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "Showndown set生成时间过长,请重新调整",
            }, JsonOptions);
        }

        var la = new LegalityAnalysis(pkm);
        var spec = GameInfo.Strings.Species[template.Species];

        // Apply standard item logic only for non-eggs
        if (!isEgg)
        {
            ApplyStandardItemLogic(pkm);
        }

        // Generate LGPE code if needed
        if (pkm is PB7)
        {
            if (pkm.Species == (int)Species.Mew && pkm.IsShiny)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    info = "Mew can **not** be Shiny in LGPE. PoGo Mew does not transfer and Pokeball Plus Mew is shiny locked.",
                }, JsonOptions);
            }
        }

        if (!la.Valid)
        {
            var reason = GetFailureReason(result, spec);
            var hint = result == "Failed" ? GetLegalizationHint(template, sav, pkm, spec) : null;
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = $"{reason} - {hint}",
            }, JsonOptions);
        }

        // Final preparation
        PrepareForTrade(pkm, set, finalLanguage);

        var isNonNative = la.EncounterOriginal.Context != pkm.Context || pkm.GO;

        if (pkm == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "Showndown set生成有问题",
            }, JsonOptions);
        }

        List<Pictocodes> lgcode = GenerateRandomPictocodes(3);


        if (pkm is not null && !pkm.CanBeTraded())
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "交易模块拒绝了你的请求",
            }, JsonOptions);
        }

        la = new LegalityAnalysis(pkm!);

        if (!la.Valid)
        {
            string responseMessage;
            if (pkm?.IsEgg == true)
            {
                string speciesName = SpeciesName.GetSpeciesName(pkm.Species, (int)LanguageID.English);
                responseMessage = $"Invalid Showdown Set for the {speciesName} egg. Please review your information and try again.\n\nLegality Report:\n```\n{la.Report()}\n```";
            }
            else
            {
                string speciesName = SpeciesName.GetSpeciesName(pkm!.Species, (int)LanguageID.English);
                responseMessage = $"{speciesName} attachment is not legal, and cannot be traded!\n\nLegality Report:\n```\n{la.Report()}\n```";
            }

            return JsonSerializer.Serialize(new
            {
                success = false,
                info = responseMessage,
            }, JsonOptions);
        }

        var disallowNonNatives = WithHub(runner,
            svHub => svHub.Config.Legality.DisallowNonNatives,
            swshHub => swshHub.Config.Legality.DisallowNonNatives,
            bdspHub => bdspHub.Config.Legality.DisallowNonNatives,
            laHub => laHub.Config.Legality.DisallowNonNatives,
            lgpeHub => lgpeHub.Config.Legality.DisallowNonNatives
        );

        if (disallowNonNatives && isNonNative)
        {
            string speciesName = SpeciesName.GetSpeciesName(pkm!.Species, (int)LanguageID.ChineseS);
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = $"This **{speciesName}** is not native to this game, and cannot be traded! Trade with the correct bot, then trade to HOME.",
            }, JsonOptions);
        }

        var disallowTracked = WithHub(runner,
            svHub => svHub.Config.Legality.DisallowTracked,
            swshHub => swshHub.Config.Legality.DisallowTracked,
            bdspHub => bdspHub.Config.Legality.DisallowTracked,
            laHub => laHub.Config.Legality.DisallowTracked,
            lgpeHub => lgpeHub.Config.Legality.DisallowTracked
        );

        if (disallowTracked && pkm is IHomeTrack { HasTracker: true })
        {
            string speciesName = SpeciesName.GetSpeciesName(pkm!.Species, (int)LanguageID.ChineseS);
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = $"This {speciesName} file is tracked by HOME, and cannot be traded!",
            }, JsonOptions);
        }

        if (!la.Valid && la.Results.Any(m => m.Identifier is CheckIdentifier.Memory))
        {
            var clone = pkm!.Clone();
            clone.HandlingTrainerName = pkm.OriginalTrainerName;
            clone.HandlingTrainerGender = pkm.OriginalTrainerGender;
            if (clone is PK8 or PA8 or PB8 or PK9)
                ((dynamic)clone).HandlingTrainerLanguage = (byte)pkm.Language;
            clone.CurrentHandler = 1;
            la = new LegalityAnalysis(clone);
            if (la.Valid) pkm = clone;
        }


        int code = runner.Config.Trade.GetRandomTradeCode();
        if ((uint)code > 9999_9999)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "Trade code should be 00000000-99999999!",
            }, JsonOptions);
        }


        var userName = "人生赢家";
        var trainer = new PokeTradeTrainerInfo(userName, userId);
        int uniqueTradeID = GenerateUniqueTradeID();
        var significance = RequestSignificance.None;
        var added = QueueResultAdd.AlreadyInQueue;

        if (pkm is PK9 pk9 && runner is PokeBotRunner<PK9> runnerPk9)
        {
            var notifier = new PokeTradeLogNotifier<PK9>();
            var detail = new PokeTradeDetail<PK9>(pk9, trainer, notifier, PokeTradeType.Specific, code, significance == RequestSignificance.Favored,
                lgcode, 1, 1, false, uniqueTradeID, false, false);
            var trade = new TradeEntry<PK9>(detail, userId, PokeRoutineType.LinkTrade, userName, uniqueTradeID);

            added = runnerPk9.Hub.Queues.Info.AddToTradeQueue(trade, userId, false,
                significance == RequestSignificance.Owner);
        } else if (pkm is PK8 pk8 && runner is PokeBotRunner<PK8> runnerPk8)
        {
            var notifier = new PokeTradeLogNotifier<PK8>();
            var detail = new PokeTradeDetail<PK8>(pk8, trainer, notifier, PokeTradeType.Specific, code, significance == RequestSignificance.Favored,
                lgcode, 1, 1, false, uniqueTradeID, false, false);
            var trade = new TradeEntry<PK8>(detail, userId, PokeRoutineType.LinkTrade, userName, uniqueTradeID);

            added = runnerPk8.Hub.Queues.Info.AddToTradeQueue(trade, userId, false,
                significance == RequestSignificance.Owner);
        } else if (pkm is PB8 pb8 && runner is PokeBotRunner<PB8> runnerPb8)
        {
            var notifier = new PokeTradeLogNotifier<PB8>();
            var detail = new PokeTradeDetail<PB8>(pb8, trainer, notifier, PokeTradeType.Specific, code, significance == RequestSignificance.Favored,
                lgcode, 1, 1, false, uniqueTradeID, false, false);
            var trade = new TradeEntry<PB8>(detail, userId, PokeRoutineType.LinkTrade, userName, uniqueTradeID);

            added = runnerPb8.Hub.Queues.Info.AddToTradeQueue(trade, userId, false,
                significance == RequestSignificance.Owner);
        } else if (pkm is PA8 pa8 && runner is PokeBotRunner<PA8> runnerPa8)
        {
            var notifier = new PokeTradeLogNotifier<PA8>();
            var detail = new PokeTradeDetail<PA8>(pa8, trainer, notifier, PokeTradeType.Specific, code, significance == RequestSignificance.Favored,
                lgcode, 1, 1, false, uniqueTradeID, false, false);
            var trade = new TradeEntry<PA8>(detail, userId, PokeRoutineType.LinkTrade, userName, uniqueTradeID);

            added = runnerPa8.Hub.Queues.Info.AddToTradeQueue(trade, userId, false,
                significance == RequestSignificance.Owner);
        }
        else if (pkm is PB7 pb7 && runner is PokeBotRunner<PB7> runnerPb7)
        {
            var notifier = new PokeTradeLogNotifier<PB7>();
            var detail = new PokeTradeDetail<PB7>(pb7, trainer, notifier, PokeTradeType.Specific, code, significance == RequestSignificance.Favored,
                lgcode, 1, 1, false, uniqueTradeID, false, false);
            var trade = new TradeEntry<PB7>(detail, userId, PokeRoutineType.LinkTrade, userName, uniqueTradeID);

            added = runnerPb7.Hub.Queues.Info.AddToTradeQueue(trade, userId, false,
                significance == RequestSignificance.Owner);
        }
        else
        {
            throw new Exception("Unknown trade type");
        }

        if (added == QueueResultAdd.AlreadyInQueue)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                info = "您已经在等待队列中了,请先完成交换",
            }, JsonOptions);
        }


        return JsonSerializer.Serialize(new
        {
            success = true,
            info = new
            {
                uniqueTradeID = uniqueTradeID,
                userId = userId,
                userName = userName,
                tradeCode = code,
                pkmSpecies = spec,
            },
        }, JsonOptions);
    }

    public static void ApplyStandardItemLogic(PKM pkm)
    {
        pkm.HeldItem = pkm switch
        {
            PA8 => (int)TradeSettings.TradeSettingsCategory.HeldItem.None,
            _ when pkm.HeldItem == 0 && !pkm.IsEgg => (int)TradeSettings.TradeSettingsCategory.HeldItem.None,
            _ => pkm.HeldItem
        };
    }

    private static int GenerateUniqueTradeID()
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int randomValue = Random.Shared.Next(1000);
        return (int)((timestamp % int.MaxValue) * 1000 + randomValue);
    }

    public static List<Pictocodes> GenerateRandomPictocodes(int count)
    {
        Random rnd = new();
        List<Pictocodes> randomPictocodes = [];
        Array pictocodeValues = Enum.GetValues<Pictocodes>();

        for (int i = 0; i < count; i++)
        {
            Pictocodes randomPictocode = (Pictocodes)pictocodeValues.GetValue(rnd.Next(pictocodeValues.Length))!;
            randomPictocodes.Add(randomPictocode);
        }

        return randomPictocodes;
    }

    public static void PrepareForTrade(PKM pk, ShowdownSet set, byte finalLanguage)
    {
        if (pk.WasEgg)
            pk.EggMetDate = pk.MetDate;

        pk.Language = finalLanguage;

        if (!set.Nickname.Equals(pk.Nickname) && string.IsNullOrEmpty(set.Nickname))
            pk.ClearNickname();

        pk.ResetPartyStats();
    }

    public static string GetLegalizationHint(IBattleTemplate template, ITrainerInfo sav, PKM pkm, string speciesName)
    {
        var hint = AutoLegalityWrapper.GetLegalizationHint(template, sav, pkm);
        if (hint.Contains("Requested shiny value (ShinyType."))
        {
            hint = $"{speciesName} **cannot** be shiny. Please try again.";
        }
        return hint;
    }

    public static string GetFailureReason(string result, string speciesName)
    {
        return result switch
        {
            "Timeout" => $"That {speciesName} set took too long to generate.",
            "VersionMismatch" => "Request refused: PKHeX and Auto-Legality Mod version mismatch.",
            _ => $"I wasn't able to create a {speciesName} from that set."
        };
    }

    private static async Task SendResponseAsync(HttpListenerResponse response, int statusCode, string content, string contentType = "text/plain")
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.Headers.Add("Cache-Control", "no-cache");

            var buffer = Encoding.UTF8.GetBytes(content ?? "");
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            await response.OutputStream.FlushAsync();
            response.Close();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 64 || ex.ErrorCode == 1229)
        {
            // Client disconnected - ignore
        }
        catch (ObjectDisposedException)
        {
            // Response already closed - ignore
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private static async Task SendBinaryResponseAsync(HttpListenerResponse response, int statusCode, byte[] content, string contentType)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = content.Length;

            await response.OutputStream.WriteAsync(content.AsMemory(0, content.Length));
            await response.OutputStream.FlushAsync();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 64 || ex.ErrorCode == 1229)
        {
            // Client disconnected - ignore
        }
        catch (ObjectDisposedException)
        {
            // Response already closed - ignore
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private static async Task TrySendErrorResponseAsync(HttpListenerResponse response, int statusCode, string message)
    {
        try
        {
            if (response.OutputStream.CanWrite)
            {
                await SendResponseAsync(response, statusCode, message);
            }
        }
        catch { }
    }

    private async Task<string> UpdateAllInstances(HttpListenerRequest request)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            bool forceUpdate = false;

            // Check if this is a status check for an existing update
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var requestData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

                    // Check for force flag
                    if (requestData?.ContainsKey("force") == true)
                    {
                        forceUpdate = requestData["force"].GetBoolean();
                    }
                }
                catch
                {
                    // Not JSON, ignore
                }
            }

            // Check if update is already in progress
            if (UpdateManager.IsUpdateInProgress())
            {
                return CreateErrorResponse("An update is already in progress");
            }

            if (RestartManager.IsRestartInProgress)
            {
                return CreateErrorResponse("Cannot update while restart is in progress");
            }

            // Start or resume update
            var session = await UpdateManager.StartOrResumeUpdateAsync(_mainForm, _tcpPort, forceUpdate);

            LogUtil.LogInfo($"Started update session with ID: {session.SessionId}, Force: {forceUpdate}", "WebServer");

            return JsonSerializer.Serialize(new
            {
                sessionId = session.SessionId,
                phase = session.Phase.ToString(),
                message = session.Message,
                totalInstances = session.TotalInstances,
                completedInstances = session.CompletedInstances,
                failedInstances = session.FailedInstances,
                startTime = session.StartTime.ToString("o"),
                success = true,
                info = "Update process started in background. Use /api/bot/update/active to check status."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to start update: {ex.Message}", "WebServer");
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> UpdateSingleInstance(HttpListenerRequest request, int port)
    {
        try
        {
            // Validate port range - ensure it's within valid TCP port range
            if (port <= 0 || port > 65535)
            {
                LogUtil.LogError($"Invalid port number: {port} (must be 1-65535)", "WebServer");
                return CreateErrorResponse($"Invalid port number: {port}. Port must be between 1 and 65535.");
            }

            // Additional validation - ensure port is not in reserved ranges
            if (port < 1024 && port != _tcpPort) // Allow only well-known ports that are explicitly configured
            {
                LogUtil.LogError($"Attempted to update instance on reserved port: {port}", "WebServer");
                return CreateErrorResponse($"Cannot update instances on reserved system ports (1-1023).");
            }

            // Check if this is the master instance trying to update itself
            if (port == _tcpPort)
            {
                LogUtil.LogError($"Master instance (port {port}) attempted to update itself. This is not supported.", "WebServer");
                return CreateErrorResponse("Master instance cannot update itself. Use /api/bot/update/all to update all instances including master.");
            }

            // Check if instance exists and is online
            var instances = await ScanRemoteInstancesAsync();
            var targetInstance = instances.FirstOrDefault(i => i.Port == port);

            if (targetInstance == null)
            {
                LogUtil.LogError($"Instance with port {port} not found", "WebServer");
                return CreateErrorResponse($"Instance with port {port} not found");
            }

            if (!targetInstance.IsOnline)
            {
                LogUtil.LogError($"Instance with port {port} is not online", "WebServer");
                return CreateErrorResponse($"Instance with port {port} is not online");
            }

            // Check if any update is already in progress
            if (UpdateManager.IsUpdateInProgress())
            {
                var currentState = UpdateManager.GetCurrentState();

                // Check if this specific instance is already being updated
                if (currentState?.Instances?.Any(i => i.TcpPort == port) == true)
                {
                    var instanceState = currentState.Instances.First(i => i.TcpPort == port);

                    LogUtil.LogInfo($"Instance {port} is already updating. Status: {instanceState.Status}", "WebServer");
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        message = $"Instance {port} is already being updated",
                        status = instanceState.Status.ToString(),
                        error = instanceState.Error
                    }, JsonOptions);
                }

                // Another update is in progress but not for this instance
                LogUtil.LogError($"Cannot update instance {port} - another update is in progress", "WebServer");
                return CreateErrorResponse("Another update is already in progress. Please wait for it to complete or clear the session.");
            }

            // Check if restart is in progress
            if (RestartManager.IsRestartInProgress)
            {
                LogUtil.LogError($"Cannot update instance {port} - restart is in progress", "WebServer");
                return CreateErrorResponse("Cannot update while restart is in progress");
            }

            // Parse request body for optional parameters
            bool forceUpdate = false;
            if (request.ContentLength64 > 0 && request.ContentLength64 < 1024) // Limit request size
            {
                using var reader = new StreamReader(request.InputStream);
                var body = await reader.ReadToEndAsync();

                var sanitizedJson = SanitizeJsonInput(body);
                if (!string.IsNullOrEmpty(sanitizedJson))
                {
                    try
                    {
                        var requestData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sanitizedJson, CachedJsonOptions.Secure);
                        if (requestData?.ContainsKey("force") == true)
                        {
                            forceUpdate = requestData["force"].GetBoolean();
                        }
                    }
                    catch (JsonException ex)
                    {
                        LogUtil.LogError($"Failed to parse request body: {ex.Message}", "WebServer");
                        // Continue without force flag
                    }
                }
            }
            else if (request.ContentLength64 >= 1024)
            {
                LogUtil.LogError($"Request body too large: {request.ContentLength64} bytes", "WebServer");
                return CreateErrorResponse("Request body too large");
            }

            LogUtil.LogInfo($"Starting single instance update for port {port} (Force: {forceUpdate})", "WebServer");

            // Start the update for the single instance
            var success = await UpdateManager.UpdateSingleInstanceAsync(_mainForm, port, _cts.Token);

            if (success)
            {
                LogUtil.LogInfo($"Successfully updated instance on port {port}", "WebServer");

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    message = $"Instance on port {port} updated successfully",
                    port,
                    processId = targetInstance.ProcessId
                }, JsonOptions);
            }
            else
            {
                LogUtil.LogError($"Failed to update instance on port {port}", "WebServer");

                return JsonSerializer.Serialize(new
                {
                    success = false,
                    message = $"Failed to update instance on port {port}",
                    port,
                    error = "Update failed - check logs for details"
                }, JsonOptions);
            }
        }
        catch (OperationCanceledException)
        {
            LogUtil.LogError($"Update for instance {port} was cancelled", "WebServer");
            return CreateErrorResponse($"Update for instance {port} was cancelled");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error updating single instance {port}: {ex.Message}", "WebServer");
            LogUtil.LogError($"Stack trace: {ex.StackTrace}", "WebServer");
            return CreateErrorResponse($"Failed to update instance: {ex.Message}");
        }
    }

    private static async Task<string> RestartAllInstances(HttpListenerRequest _)
    {
        try
        {
            var result = await RestartManager.TriggerManualRestartAsync();

            return JsonSerializer.Serialize(new
            {
                result.Success,
                result.TotalInstances,
                result.MasterRestarting,
                result.Error,
                Reason = result.Reason.ToString(),
                Results = result.InstanceResults.Select(r => new
                {
                    r.Port,
                    r.ProcessId,
                    r.Success,
                    r.Error
                }),
                Message = result.Success ? "Restart completed successfully" : "Restart failed"
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private static async Task<string> UpdateRestartSchedule(HttpListenerRequest request)
    {
        try
        {
            if (request.HttpMethod == "GET")
            {
                var config = RestartManager.GetScheduleConfig();
                var nextRestart = RestartManager.NextScheduledRestart;

                var response = new
                {
                    config.Enabled,
                    config.Time,
                    NextRestart = nextRestart?.ToString("yyyy-MM-dd HH:mm:ss"),
                    RestartManager.IsRestartInProgress,
                    CurrentState = RestartManager.CurrentState.ToString()
                };

                return JsonSerializer.Serialize(response);
            }
            else if (request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(request.InputStream);
                var body = await reader.ReadToEndAsync();

                var config = JsonSerializer.Deserialize<RestartScheduleConfig>(body, JsonOptions);
                if (config == null)
                {
                    LogUtil.LogError("Failed to deserialize RestartScheduleConfig", "WebServer");
                    return CreateErrorResponse("Invalid schedule configuration");
                }

                RestartManager.UpdateScheduleConfig(config);

                var result = new
                {
                    Success = true,
                    Message = "Restart schedule updated successfully",
                    NextRestart = RestartManager.NextScheduledRestart?.ToString("yyyy-MM-dd HH:mm:ss")
                };

                return JsonSerializer.Serialize(result);
            }

            LogUtil.LogError($"Invalid HTTP method: {request.HttpMethod}", "WebServer");
            return CreateErrorResponse("Invalid method");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in UpdateRestartSchedule: {ex.Message}", "WebServer");
            LogUtil.LogError($"Stack trace: {ex.StackTrace}", "WebServer");
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> GetIdleStatusAsync()
    {
        try
        {
            var instances = new List<InstanceIdleInfo>();

            // Get local instance idle status
            var localInfo = GetLocalIdleInfo();
            instances.Add(localInfo);

            // Get remote instances idle status
            var remoteInstances = (await ScanRemoteInstancesAsync()).Where(i => i.IsOnline);
            instances.AddRange(GetRemoteIdleInfo(remoteInstances));

            var response = new IdleStatusResponse
            {
                Instances = instances,
                TotalBots = instances.Sum(i => i.TotalBots),
                TotalIdleBots = instances.Sum(i => i.IdleBots),
                AllBotsIdle = instances.All(i => i.AllIdle)
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private InstanceIdleInfo GetLocalIdleInfo()
    {
        var localBots = GetBotControllers();
        var config = GetConfig();
        var nonIdleBots = new List<NonIdleBot>();
        var idleCount = 0;

        foreach (var controller in localBots)
        {
            var status = controller.ReadBotState();
            var upperStatus = status?.ToUpper() ?? "";

            if (upperStatus == "IDLE" || upperStatus == "STOPPED")
            {
                idleCount++;
            }
            else
            {
                nonIdleBots.Add(new NonIdleBot
                {
                    Name = GetBotName(controller.State, config),
                    Status = status ?? "Unknown"
                });
            }
        }

        return new InstanceIdleInfo
        {
            Port = _tcpPort,
            ProcessId = Environment.ProcessId,
            TotalBots = localBots.Count,
            IdleBots = idleCount,
            NonIdleBots = nonIdleBots,
            AllIdle = idleCount == localBots.Count
        };
    }

    private static List<InstanceIdleInfo> GetRemoteIdleInfo(IEnumerable<BotInstance> remoteInstances)
    {
        var instances = new List<InstanceIdleInfo>();

        foreach (var instance in remoteInstances)
        {
            try
            {
                var botsResponse = QueryRemote(instance.Port, "LISTBOTS");
                if (botsResponse.StartsWith('{') && botsResponse.Contains("Bots"))
                {
                    var botsData = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object>>>>(botsResponse);
                    if (botsData?.ContainsKey("Bots") == true)
                    {
                        var bots = botsData["Bots"];
                        var idleCount = 0;
                        var nonIdleBots = new List<NonIdleBot>();

                        foreach (var bot in bots)
                        {
                            if (bot.TryGetValue("Status", out var status))
                            {
                                var statusStr = status?.ToString()?.ToUpperInvariant() ?? "";
                                if (statusStr == "IDLE" || statusStr == "STOPPED")
                                {
                                    idleCount++;
                                }
                                else
                                {
                                    nonIdleBots.Add(new NonIdleBot
                                    {
                                        Name = bot.TryGetValue("Name", out var name) ? name?.ToString() ?? "Unknown" : "Unknown",
                                        Status = statusStr
                                    });
                                }
                            }
                        }

                        instances.Add(new InstanceIdleInfo
                        {
                            Port = instance.Port,
                            ProcessId = instance.ProcessId,
                            TotalBots = bots.Count,
                            IdleBots = idleCount,
                            NonIdleBots = nonIdleBots,
                            AllIdle = idleCount == bots.Count
                        });
                    }
                }
            }
            catch { }
        }

        return instances;
    }

    private static async Task<string> CheckForUpdates()
    {
        try
        {
            var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
            var changelog = await UpdateChecker.FetchChangelogAsync();

            var response = new UpdateCheckResponse
            {
                Version = latestVersion ?? "Unknown",
                Changelog = changelog,
                Available = updateAvailable
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            var response = new UpdateCheckResponse
            {
                Version = "Unknown",
                Changelog = "Unable to fetch update information",
                Available = false,
                Error = ex.Message
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
    }

    private static int ExtractPort(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return 0;

            var parts = path.Split('/');
            if (parts.Length <= 4)
                return 0;

            var portString = parts[4];

            // Validate port string length to prevent overflow
            if (portString.Length > 10)
                return 0;

            if (int.TryParse(portString, out var port))
            {
                // Validate port range
                if (port > 0 && port <= 65535)
                    return port;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<string> GetInstancesAsync()
    {
        var remoteInstances = await ScanRemoteInstancesAsync();
        var response = new InstancesResponse
        {
            Instances = [CreateLocalInstance(), .. remoteInstances]
        };
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private BotInstance CreateLocalInstance()
    {
        var config = GetConfig();
        var controllers = GetBotControllers();

        var mode = config?.Mode.ToString() ?? "Unknown";
        var name = config?.Hub?.BotName ?? "PokeBot";

        var version = "Unknown";
        try
        {
            var tradeBotType = Type.GetType("SysBot.Pokemon.Helpers.PokeBot, SysBot.Pokemon");
            if (tradeBotType != null)
            {
                var versionField = tradeBotType.GetField("Version",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (versionField != null)
                {
                    version = versionField.GetValue(null)?.ToString() ?? "Unknown";
                }
            }

            if (version == "Unknown")
            {
                version = _mainForm.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";
            }
        }
        catch
        {
            version = _mainForm.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }

        var botStatuses = controllers.Select(c => new BotStatusInfo
        {
            Name = GetBotName(c.State, config),
            Status = c.ReadBotState()
        }).ToList();

        return new BotInstance
        {
            ProcessId = Environment.ProcessId,
            Name = name,
            Port = _tcpPort,
            WebPort = _port,
            Version = version,
            Mode = mode,
            BotCount = botStatuses.Count,
            IsOnline = true,
            IsMaster = IsMasterInstance(),
            BotStatuses = botStatuses,
            ProcessPath = Environment.ProcessPath
        };
    }

    private async Task<List<BotInstance>> ScanRemoteInstancesAsync()
    {
        var instances = new List<BotInstance>();
        var currentPid = Environment.ProcessId;
        var discoveredPorts = new HashSet<int> { _tcpPort }; // Exclude current instance port

        // Method 1: Scan TCP ports with throttling to avoid system overload
        // Only scan a smaller range by default to avoid timeout
        const int startPort = 8081;
        const int endPort = 8090; // Reduced from 8181 to 8090 for faster scanning
        const int maxConcurrentScans = 5; // Throttle concurrent connections

        using var semaphore = new SemaphoreSlim(maxConcurrentScans, maxConcurrentScans);
        var tasks = new List<Task>();

        for (int port = startPort; port <= endPort; port++)
        {
            if (port == _tcpPort)
                continue;

            int capturedPort = port; // Capture for closure
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Port check with more reasonable timeout for slower systems
                    using var client = new TcpClient();
                    client.ReceiveTimeout = 500; // Increased from 200ms to 500ms
                    client.SendTimeout = 500;

                    var connectTask = client.ConnectAsync("127.0.0.1", capturedPort);
                    var timeoutTask = Task.Delay(500);
                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask || !client.Connected)
                        return;

                    using var stream = client.GetStream();
                    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    await writer.WriteLineAsync("INFO");
                    await writer.FlushAsync();

                    // Read response with timeout
                    stream.ReadTimeout = 1000; // Increased from 500ms to 1000ms
                    var response = await reader.ReadLineAsync();

                    if (!string.IsNullOrEmpty(response) && response.StartsWith('{'))
                    {
                        // This is a PokeBot instance - find the process ID
                        int processId = FindProcessIdForPort(capturedPort);

                        var instance = new BotInstance
                        {
                            ProcessId = processId,
                            Name = "PokeBot",
                            Port = capturedPort,
                            WebPort = 8080,
                            Version = "Unknown",
                            Mode = "Unknown",
                            BotCount = 0,
                            IsOnline = true,
                            IsMaster = false, // Will be determined by who's hosting web server
                            ProcessPath = GetProcessPathForId(processId)
                        };

                        // Update instance info from the response
                        UpdateInstanceInfo(instance, capturedPort);

                        lock (instances) // Thread-safe addition
                        {
                            instances.Add(instance);
                        }
                        discoveredPorts.Add(capturedPort);
                    }
                }
                catch { /* Port not open or not a PokeBot instance */ }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        // Wait for all port scans to complete with overall timeout
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Method 2: Check local PokeBot processes (fallback for instances not in standard port range)
        try
        {
            var processes = Process.GetProcessesByName("PokeBot")
                .Where(p => p.Id != currentPid);

            foreach (var process in processes)
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                        continue;

                    var portFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"PokeBot_{process.Id}.port");
                    if (!File.Exists(portFile))
                        continue;

                    var portText = File.ReadAllText(portFile).Trim();
                    if (portText.StartsWith("ERROR:"))
                        continue;

                    // Port file now only contains TCP port
                    var lines = portText.Split('\n', '\r').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
                        continue;

                    // Skip if already discovered
                    if (discoveredPorts.Contains(port))
                        continue;

                    var isOnline = IsPortOpen(port);
                    var instance = new BotInstance
                    {
                        ProcessId = process.Id,
                        Name = "PokeBot",
                        Port = port,
                        WebPort = 8080,
                        Version = "Unknown",
                        Mode = "Unknown",
                        BotCount = 0,
                        IsOnline = isOnline,
                        ProcessPath = exePath
                    };

                    if (isOnline)
                    {
                        UpdateInstanceInfo(instance, port);
                    }

                    instances.Add(instance);
                    discoveredPorts.Add(port);
                }
                catch { /* Ignore */ }
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error scanning local processes: {ex.Message}", "WebServer");
        }

        return instances;
    }

    /// <summary>
    /// Find process ID for a given port by checking port files
    /// </summary>
    private static int FindProcessIdForPort(int port)
    {
        try
        {
            var processes = Process.GetProcessesByName("PokeBot");
            foreach (var proc in processes)
            {
                try
                {
                    var exePath = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                        continue;

                    var portFile = Path.Combine(Path.GetDirectoryName(exePath)!, $"PokeBot_{proc.Id}.port");
                    if (File.Exists(portFile))
                    {
                        var portText = File.ReadAllText(portFile).Trim();
                        if (int.TryParse(portText, out var filePort) && filePort == port)
                        {
                            return proc.Id;
                        }
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

        return 0; // Process not found
    }

    /// <summary>
    /// Get process path for a given process ID
    /// </summary>
    private static string? GetProcessPathForId(int processId)
    {
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateInstanceInfo(BotInstance instance, int port)
    {
        try
        {
            var infoResponse = QueryRemote(port, "INFO");
            if (infoResponse.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(infoResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("Version", out var version))
                    instance.Version = version.GetString() ?? "Unknown";

                if (root.TryGetProperty("Mode", out var mode))
                    instance.Mode = mode.GetString() ?? "Unknown";

                if (root.TryGetProperty("Name", out var name))
                    instance.Name = name.GetString() ?? "PokeBot";

                if (root.TryGetProperty("ProcessPath", out var processPath))
                    instance.ProcessPath = processPath.GetString();
            }

            var botsResponse = QueryRemote(port, "LISTBOTS");
            if (botsResponse.StartsWith('{') && botsResponse.Contains("Bots"))
            {
                var botsData = JsonSerializer.Deserialize<Dictionary<string, List<BotInfo>>>(botsResponse);
                if (botsData?.ContainsKey("Bots") == true)
                {
                    instance.BotCount = botsData["Bots"].Count;
                    instance.BotStatuses = [.. botsData["Bots"].Select(b => new BotStatusInfo
                    {
                        Name = b.Name,
                        Status = b.Status
                    })];
                }
            }
        }
        catch { }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            // Validate port range
            if (port <= 0 || port > 65535)
                return false;

            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            if (success)
            {
                client.EndConnect(result);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private string GetBots(int port)
    {
        try
        {
            if (port == _tcpPort)
            {
                var config = GetConfig();
                var controllers = GetBotControllers();

                var response = new BotsResponse
                {
                    Bots = [.. controllers.Select(c => new BotInfo
                    {
                        Id = $"{c.State.Connection.IP}:{c.State.Connection.Port}",
                        Name = GetBotName(c.State, config),
                        RoutineType = c.State.InitialRoutine.ToString(),
                        Status = c.ReadBotState(),
                        ConnectionType = c.State.Connection.Protocol.ToString(),
                        IP = c.State.Connection.IP,
                        Port = c.State.Connection.Port
                    })]
                };

                var json = JsonSerializer.Serialize(response, JsonOptions);
                LogUtil.LogInfo($"GetBots returning {json.Length} bytes for port {port}", "WebAPI");
                return json;
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error in GetBots for port {port}: {ex.Message}", "WebAPI");
            return JsonSerializer.Serialize(new BotsResponse
            {
                Bots = [],
                Error = $"Error getting bots: {ex.Message}"
            }, JsonOptions);
        }

        // Query remote instance
        var result = QueryRemote(port, "LISTBOTS");

        // Check if the result is an error
        if (result.StartsWith("ERROR"))
        {
            return JsonSerializer.Serialize(new BotsResponse
            {
                Bots = [],
                Error = result
            }, JsonOptions);
        }

        // If it's already valid JSON, return it
        // Otherwise wrap it in a valid response
        try
        {
            // Try to parse to validate it's JSON
            using var doc = JsonDocument.Parse(result);
            return result;
        }
        catch
        {
            // If not valid JSON, return empty bot list with error
            return JsonSerializer.Serialize(new BotsResponse
            {
                Bots = [],
                Error = "Invalid response from remote instance"
            }, JsonOptions);
        }
    }

    private async Task<string> RunCommand(HttpListenerRequest request, int port)
    {
        try
        {
            var commandRequest = await DeserializeRequestAsync<BotCommandRequest>(request);
            if (commandRequest == null)
                return CreateErrorResponse("Invalid command request");

            if (port == _tcpPort)
            {
                return RunLocalCommand(commandRequest.Command);
            }

            var tcpCommand = $"{commandRequest.Command}All".ToUpper();
            var result = QueryRemote(port, tcpCommand);

            var response = new CommandResponse
            {
                Message = result,
                Port = port,
                Command = commandRequest.Command,
                Error = result.StartsWith("ERROR") ? result : null
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<string> RunAllCommandAsync(HttpListenerRequest request)
    {
        try
        {
            var commandRequest = await DeserializeRequestAsync<BotCommandRequest>(request);
            if (commandRequest == null)
                return CreateErrorResponse("Invalid command request");

            var results = await ExecuteCommandOnAllInstancesAsync(commandRequest.Command);

            var response = new BatchCommandResponse
            {
                Results = results,
                TotalInstances = results.Count,
                SuccessfulCommands = results.Count(r => r.Success)
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<List<CommandResponse>> ExecuteCommandOnAllInstancesAsync(string command)
    {
        var tasks = new List<Task<CommandResponse?>>
        {
            // Execute local command
            Task.Run(() =>
            {
                var localResult = JsonSerializer.Deserialize<CommandResponse>(RunLocalCommand(command), JsonOptions);
                if (localResult != null)
                {
                    localResult.InstanceName = _mainForm.Text;
                }
                return localResult;
            })
        };

        // Execute remote commands
        var remoteInstances = (await ScanRemoteInstancesAsync()).Where(i => i.IsOnline);
        foreach (var instance in remoteInstances)
        {
            var instanceCopy = instance; // Capture for closure
            tasks.Add(Task.Run<CommandResponse?>(() =>
            {
                try
                {
                    var result = QueryRemote(instanceCopy.Port, $"{command}All".ToUpper());
                    return new CommandResponse
                    {
                        Message = result,
                        Port = instanceCopy.Port,
                        Command = command,
                        InstanceName = instanceCopy.Name,
                        Error = result.StartsWith("ERROR") ? result : null
                    };
                }
                catch (Exception ex)
                {
                    return new CommandResponse
                    {
                        Error = ex.Message,
                        Port = instanceCopy.Port,
                        Command = command,
                        InstanceName = instanceCopy.Name
                    };
                }
            }));
        }

        var results = await Task.WhenAll(tasks);
        return [.. results.Where(r => r != null).Cast<CommandResponse>()];
    }

    private string RunLocalCommand(string command)
    {
        try
        {
            var cmd = MapCommand(command);
            ExecuteMainFormCommand(cmd);

            var response = new CommandResponse
            {
                Message = $"Command {command} sent successfully",
                Port = _tcpPort,
                Command = command
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error executing local command {command}: {ex.Message}", "WebServer");
            // Still return success as the command was queued
            var response = new CommandResponse
            {
                Message = $"Command {command} queued",
                Port = _tcpPort,
                Command = command
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
    }

    private void ExecuteMainFormCommand(BotControlCommand command)
    {
        _mainForm.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
        {
            const string methodName = "SendAll";
            if (AllowedMethods.Contains(methodName))
            {
                var sendAllMethod = _mainForm.GetType().GetMethod(methodName,
                    BindingFlags.NonPublic | BindingFlags.Instance);
                sendAllMethod?.Invoke(_mainForm, [command]);
            }
        }));
    }

    private static BotControlCommand MapCommand(string webCommand)
    {
        return webCommand.ToLower() switch
        {
            "start" => BotControlCommand.Start,
            "stop" => BotControlCommand.Stop,
            "idle" => BotControlCommand.Idle,
            "resume" => BotControlCommand.Resume,
            "restart" => BotControlCommand.Restart,
            "reboot" => BotControlCommand.RebootAndStop,
            "screenon" => BotControlCommand.ScreenOnAll,
            "screenoff" => BotControlCommand.ScreenOffAll,
            _ => BotControlCommand.None
        };
    }

    public static string QueryRemote(int port, string command)
    {
        try
        {
            // Validate inputs
            if (port <= 0 || port > 65535)
            {
                return "ERROR: Invalid port number";
            }

            if (string.IsNullOrWhiteSpace(command) || command.Length > 1000)
            {
                return "ERROR: Invalid command";
            }

            // Sanitize command to prevent injection
            var sanitizedCommand = SanitizeCommand(command);
            if (sanitizedCommand == null)
            {
                return "ERROR: Command contains invalid characters";
            }

            using var client = new TcpClient();
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            var connectTask = client.ConnectAsync("127.0.0.1", port);
            if (!connectTask.Wait(5000))
            {
                return "ERROR: Connection timeout";
            }

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            writer.WriteLine(sanitizedCommand);
            var response = reader.ReadLine();

            // Limit response size to prevent DoS
            if (response != null && response.Length > 10000)
            {
                response = string.Concat(response.AsSpan(0, 10000), "... [truncated]");
            }

            return response ?? "ERROR: No response";
        }
        catch (Exception ex)
        {
            // Sanitize exception message to prevent information disclosure
            var sanitizedMessage = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            return $"ERROR: {sanitizedMessage}";
        }
    }

    private List<BotController> GetBotControllers()
    {
        var flpBotsField = _mainForm.GetType().GetField("FLP_Bots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (flpBotsField?.GetValue(_mainForm) is FlowLayoutPanel flpBots)
        {
            return [.. flpBots.Controls.OfType<BotController>()];
        }

        return [];
    }

    private ProgramConfig? GetConfig()
    {
        var configProp = _mainForm.GetType().GetProperty("Config",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return configProp?.GetValue(_mainForm) as ProgramConfig;
    }

    private static string GetBotName(PokeBotState state, ProgramConfig? _)
    {
        return state.Connection.IP;
    }

    private static string CreateErrorResponse(string message)
    {
        return JsonSerializer.Serialize(ApiResponseFactory.CreateSimpleError(message), JsonOptions);
    }


    private bool IsMasterInstance()
    {
        // Master is the instance hosting the web server on the configured control panel port
        var configuredPort = _mainForm.Config?.Hub?.WebServer?.ControlPanelPort ?? 8080;
        return _port == configuredPort;
    }


    private static bool IsAllowedOrigin(string origin)
    {
        // Define allowed origins - adjust based on your security requirements
        var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http://localhost",
            "https://localhost",
            "http://127.0.0.1",
            "https://127.0.0.1"
        };

        // Allow localhost with any port
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            if (uri.Host == "localhost" || uri.Host == "127.0.0.1")
            {
                return true;
            }
        }

        return allowedOrigins.Contains(origin);
    }


    private static string ClearUpdateSession()
    {
        try
        {
            // First try to force complete if there's a stuck session
            var currentState = UpdateManager.GetCurrentState();
            if (currentState != null)
            {
                // Check if master instance actually updated
                var currentVersion = SysBot.Pokemon.Helpers.PokeBot.Version;
                LogUtil.LogInfo($"Checking session state: current={currentVersion}, target={currentState.TargetVersion}, isComplete={currentState.IsComplete}", "WebServer");

                // If version matches target, force complete regardless of what the state says
                if (currentVersion == currentState.TargetVersion)
                {
                    if (!currentState.IsComplete || !currentState.Success)
                    {
                        LogUtil.LogInfo("Force completing update session - version matches target", "WebServer");
                        UpdateManager.ForceCompleteSession();
                        return JsonSerializer.Serialize(new {
                            success = true,
                            message = "Update completed successfully - all instances updated to target version",
                            action = "force_completed",
                            currentVersion,
                            targetVersion = currentState.TargetVersion
                        }, JsonOptions);
                    }
                    else
                    {
                        // Already complete and successful
                        UpdateManager.ClearState();
                        return JsonSerializer.Serialize(new {
                            success = true,
                            message = "Update was already successful - session cleared",
                            action = "cleared",
                            currentVersion
                        }, JsonOptions);
                    }
                }
                else
                {
                    LogUtil.LogInfo($"Version mismatch - clearing session (current={currentVersion}, target={currentState.TargetVersion})", "WebServer");
                }
            }

            // Clear the session
            UpdateManager.ClearState();
            return JsonSerializer.Serialize(new {
                success = true,
                message = "Update session cleared",
                action = "cleared"
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error clearing update session: {ex.Message}", "WebServer");
            return CreateErrorResponse(ex.Message);
        }
    }

    private static string GetActiveUpdates()
    {
        try
        {
            var session = UpdateManager.GetCurrentState();
            if (session == null)
            {
                return JsonSerializer.Serialize(new { active = false }, JsonOptions);
            }

            var response = new
            {
                active = true,
                session = new
                {
                    id = session.SessionId,
                    phase = session.Phase.ToString(),
                    message = session.Message,
                    totalInstances = session.TotalInstances,
                    completedInstances = session.CompletedInstances,
                    failedInstances = session.FailedInstances,
                    isComplete = session.IsComplete,
                    success = session.Success,
                    startTime = session.StartTime.ToString("o"),
                    targetVersion = session.TargetVersion,
                    currentUpdatingInstance = session.CurrentUpdatingInstance,
                    idleProgress = session.IdleProgress != null ? new
                    {
                        startTime = session.IdleProgress.StartTime.ToString("o"),
                        totalBots = session.IdleProgress.TotalBots,
                        idleBots = session.IdleProgress.IdleBots,
                        allIdle = session.IdleProgress.AllIdle,
                        elapsedSeconds = (int)session.IdleProgress.ElapsedTime.TotalSeconds,
                        remainingSeconds = Math.Max(0, (int)session.IdleProgress.TimeRemaining.TotalSeconds),
                        instances = session.IdleProgress.Instances.Select(i => new
                        {
                            tcpPort = i.TcpPort,
                            name = i.Name,
                            isMaster = i.IsMaster,
                            totalBots = i.TotalBots,
                            idleBots = i.IdleBots,
                            allIdle = i.AllIdle,
                            nonIdleBots = i.NonIdleBots
                        }).ToList()
                    } : null,
                    instances = session.Instances.Select(i => new
                    {
                        tcpPort = i.TcpPort,
                        processId = i.ProcessId,
                        isMaster = i.IsMaster,
                        status = i.Status.ToString(),
                        error = i.Error,
                        retryCount = i.RetryCount,
                        updateStartTime = i.UpdateStartTime?.ToString("o"),
                        updateEndTime = i.UpdateEndTime?.ToString("o"),
                        version = i.Version
                    }).ToList()
                }
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error getting active updates: {ex.Message}", "WebServer");
            return CreateErrorResponse(ex.Message);
        }
    }

    private static byte[]? GetIconBytes()
    {
        try
        {
            // First try to find icon.ico in the executable directory
            var exePath = Application.ExecutablePath;
            var exeDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            var iconPath = Path.Combine(exeDir, "icon.ico");

            if (File.Exists(iconPath))
            {
                return File.ReadAllBytes(iconPath);
            }

            // If not found, try to extract from embedded resources
            var assembly = Assembly.GetExecutingAssembly();
            var iconStream = assembly.GetManifestResourceStream("SysBot.Pokemon.WinForms.icon.ico");

            if (iconStream != null)
            {
                using (iconStream)
                {
                    var buffer = new byte[iconStream.Length];
                    iconStream.ReadExactly(buffer);
                    return buffer;
                }
            }

            // Try to get the application icon as a fallback
            var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                using var ms = new MemoryStream();
                icon.Save(ms);
                return ms.ToArray();
            }

            return null;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to load icon: {ex.Message}", "WebServer");
            return null;
        }
    }


    private static async Task<T?> DeserializeRequestAsync<T>(HttpListenerRequest request) where T : class
    {
        try
        {
            // Limit request size to prevent DoS attacks
            if (request.ContentLength64 > 10000)
                return null;

            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();

            var sanitizedJson = SanitizeJsonInput(body);
            if (sanitizedJson == null)
                return null;

            return JsonSerializer.Deserialize<T>(sanitizedJson, CachedJsonOptions.Secure);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sanitize query parameters to prevent injection attacks
    /// </summary>
    private static string? SanitizeQueryParameter(string? parameter, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return null;

        if (parameter.Length > maxLength)
            parameter = parameter[..maxLength];

        // Remove potentially dangerous characters
        var sanitized = CleanupRegex().Replace(parameter, "");

        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }




    private async Task<string> HandleRemoteButton(HttpListenerRequest request, int port)
    {
        try
        {
            var requestData = await DeserializeRequestAsync<RemoteButtonRequest>(request);
            if (requestData == null)
                return CreateErrorResponse("Invalid remote button request");

            // Send button command via TCP to the target instance
            var tcpCommand = $"REMOTE_BUTTON:{requestData.Button}:{requestData.BotIndex}";

            if (port == _tcpPort)
            {
                // Local command - execute directly
                return await ExecuteLocalRemoteButton(requestData.Button, requestData.BotIndex);
            }

            // Remote command
            var result = QueryRemote(port, tcpCommand);

            return JsonSerializer.Serialize(new
            {
                success = !result.StartsWith("ERROR"),
                message = result,
                port,
                button = requestData.Button
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Failed to send button command: {ex.Message}");
        }
    }

    private async Task<string> HandleRemoteMacro(HttpListenerRequest request, int port)
    {
        try
        {
            var requestData = await DeserializeRequestAsync<RemoteMacroRequest>(request);
            if (requestData == null)
                return CreateErrorResponse("Invalid remote macro request");

            // Send macro command via TCP to the target instance
            var tcpCommand = $"REMOTE_MACRO:{requestData.Macro}:{requestData.BotIndex}";

            if (port == _tcpPort)
            {
                // Local command - execute directly
                return await ExecuteLocalRemoteMacro(requestData.Macro, requestData.BotIndex);
            }

            // Remote command
            var result = QueryRemote(port, tcpCommand);

            return JsonSerializer.Serialize(new
            {
                success = !result.StartsWith("ERROR"),
                message = result,
                port,
                macro = requestData.Macro
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Failed to execute macro: {ex.Message}");
        }
    }

    private async Task<string> ExecuteLocalRemoteButton(string button, int botIndex = 0)
    {
        try
        {
            // Get all bot controllers
            var controllers = await Task.Run(() =>
            {
                if (_mainForm.InvokeRequired)
                {
                    return _mainForm.Invoke(() =>
                        _mainForm.Controls.Find("FLP_Bots", true).FirstOrDefault()?.Controls
                            .OfType<BotController>()
                            .ToList()
                    ) as List<BotController> ?? [];
                }
                return _mainForm.Controls.Find("FLP_Bots", true).FirstOrDefault()?.Controls
                    .OfType<BotController>()
                    .ToList() ?? [];
            });

            if (controllers.Count == 0)
                return CreateErrorResponse("No bots available");

            // Validate bot index
            if (botIndex < 0 || botIndex >= controllers.Count)
                return CreateErrorResponse($"Invalid bot index: {botIndex}");

            var botSource = controllers[botIndex].GetBot();
            if (botSource?.Bot == null)
                return CreateErrorResponse($"Bot at index {botIndex} not available");

            if (!botSource.IsRunning)
                return CreateErrorResponse($"Bot at index {botIndex} is not running");

            var bot = botSource.Bot;
            if (bot.Connection is not ISwitchConnectionAsync connection)
                return CreateErrorResponse("Bot connection not available");

            var switchButton = MapButtonToSwitch(button);
            if (switchButton == null)
                return CreateErrorResponse($"Invalid button: {button}");

            var cmd = SwitchCommand.Click(switchButton.Value);
            await connection.SendAsync(cmd, CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = $"Button {button} pressed on bot {botIndex}",
                button,
                botIndex
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Failed to execute button press: {ex.Message}");
        }
    }

    private async Task<string> ExecuteLocalRemoteMacro(string macro, int botIndex = 0)
    {
        try
        {
            // Get all bot controllers
            var controllers = await Task.Run(() =>
            {
                if (_mainForm.InvokeRequired)
                {
                    return _mainForm.Invoke(() =>
                        _mainForm.Controls.Find("FLP_Bots", true).FirstOrDefault()?.Controls
                            .OfType<BotController>()
                            .ToList()
                    ) as List<BotController> ?? [];
                }
                return _mainForm.Controls.Find("FLP_Bots", true).FirstOrDefault()?.Controls
                    .OfType<BotController>()
                    .ToList() ?? [];
            });

            if (controllers.Count == 0)
                return CreateErrorResponse("No bots available");

            // Validate bot index
            if (botIndex < 0 || botIndex >= controllers.Count)
                return CreateErrorResponse($"Invalid bot index: {botIndex}");

            var botSource = controllers[botIndex].GetBot();
            if (botSource?.Bot == null)
                return CreateErrorResponse($"Bot at index {botIndex} not available");

            if (!botSource.IsRunning)
                return CreateErrorResponse($"Bot at index {botIndex} is not running");

            var bot = botSource.Bot;
            if (bot.Connection is not ISwitchConnectionAsync connection)
                return CreateErrorResponse("Bot connection not available");

            var commands = ParseMacroCommands(macro);
            foreach (var cmd in commands)
            {
                if (cmd.StartsWith('d'))
                {
                    // Delay command
                    if (int.TryParse(cmd[1..], out int delay))
                    {
                        await Task.Delay(delay);
                    }
                }
                else
                {
                    // Button command
                    var parts = cmd.Split(':', 2);
                    var buttonName = parts[0];

                    var switchButton = MapButtonToSwitch(buttonName);
                    if (switchButton != null)
                    {
                        var command = SwitchCommand.Click(switchButton.Value);
                        await connection.SendAsync(command, CancellationToken.None);
                        await Task.Delay(100); // Small delay between button presses
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = $"Macro executed successfully on bot {botIndex}",
                commandCount = commands.Count,
                botIndex
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Failed to execute macro: {ex.Message}");
        }
    }

    private static SwitchButton? MapButtonToSwitch(string button)
    {
        return button.ToUpper() switch
        {
            "A" => SwitchButton.A,
            "B" => SwitchButton.B,
            "X" => SwitchButton.X,
            "Y" => SwitchButton.Y,
            "L" => SwitchButton.L,
            "R" => SwitchButton.R,
            "ZL" => SwitchButton.ZL,
            "ZR" => SwitchButton.ZR,
            "PLUS" or "+" => SwitchButton.PLUS,
            "MINUS" or "-" => SwitchButton.MINUS,
            "LSTICK" or "LTS" => SwitchButton.LSTICK,
            "RSTICK" or "RTS" => SwitchButton.RSTICK,
            "HOME" or "H" => SwitchButton.HOME,
            "CAPTURE" or "SS" => SwitchButton.CAPTURE,
            "UP" or "DUP" => SwitchButton.DUP,
            "DOWN" or "DDOWN" => SwitchButton.DDOWN,
            "LEFT" or "DLEFT" => SwitchButton.DLEFT,
            "RIGHT" or "DRIGHT" => SwitchButton.DRIGHT,
            _ => null
        };
    }

    private static List<string> ParseMacroCommands(string macro)
    {
        return [.. macro.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim())];
    }

    private class RemoteButtonRequest
    {
        public string Button { get; set; } = "";
        public int BotIndex { get; set; } = 0;
    }

    private class RemoteMacroRequest
    {
        public string Macro { get; set; } = "";
        public int BotIndex { get; set; } = 0;
    }

    /// <summary>
    /// Sanitize command strings to prevent injection attacks
    /// </summary>
    private static string? SanitizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        // Allow only alphanumeric characters, underscores, colons, and common command separators
        // This whitelist approach is more secure than blacklisting
        var allowedPattern = @"^[a-zA-Z0-9_:.-]+$";
        if (!System.Text.RegularExpressions.Regex.IsMatch(command, allowedPattern))
            return null;

        // Additional validation for known command patterns
        var validCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INFO", "LISTBOTS", "IDLEALL", "UPDATE", "STARTALL", "STOPALL",
            "RESUMEALL", "RESTARTALL", "REBOOTALL", "SCREENONALL", "SCREENOFFALL"
        };

        // Check if it's a basic command or a compound command
        var parts = command.Split(':');
        var baseCommand = parts[0].ToUpperInvariant();

        if (validCommands.Contains(baseCommand) ||
            baseCommand.StartsWith("REMOTE_") && parts.Length <= 3)
        {
            return command;
        }

        return null;
    }

    /// <summary>
    /// Sanitize JSON input to prevent injection attacks
    /// </summary>
    private static string? SanitizeJsonInput(string json, int maxLength = 10000)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        if (json.Length > maxLength)
            return null;

        try
        {
            // Validate JSON structure by attempting to parse it
            using var document = JsonDocument.Parse(json);

            // Check for excessive nesting depth
            if (GetJsonDepth(document.RootElement) > 10)
                return null;

            return json;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Calculate JSON nesting depth to prevent deeply nested attacks
    /// </summary>
    private static int GetJsonDepth(JsonElement element, int currentDepth = 0)
    {
        if (currentDepth > 10) // Prevent stack overflow from deeply nested JSON
            return currentDepth;

        var maxDepth = currentDepth;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var depth = GetJsonDepth(property.Value, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, depth);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var depth = GetJsonDepth(item, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, depth);
            }
        }

        return maxDepth;
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
