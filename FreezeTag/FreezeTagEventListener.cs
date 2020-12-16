using Impostor.Api.Events;
using Impostor.Api.Events.Player;
using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.Customization;
using Impostor.Api.Net;
using Impostor.Api.Net.Inner.Objects;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FreezeTag
{
    public class FreezeTagEventListener : IEventListener
    {
        private readonly List<IGame> DeactivatedGames = new List<IGame>();
        private readonly Dictionary<IGame, FreezeTagInfos> CodeAndInfos = new Dictionary<IGame, FreezeTagInfos>();
        private readonly ILogger<FreezeTagPlugin> _logger;

        private const string commandPrefix = "/ftag";
        private const string gameModeExplanation = "Freeze Tag is a custom Among Us mode." +
                        "\nImpostors are red, crewmates are green. The impostors can freeze the crewmates by standing near them." +
                        "\nThe crewmates can unfreeze the frozen crewmates by standing near of them." +
                        "\nObjective of the impostors: freeze everyone." +
                        "\nObjective of the crewmates: finish all their tasks." +
                        "\nIf a crewmate gets killed, the impostors get killed!";

        private const float freezeRange = 0.2f;

        public FreezeTagEventListener(ILogger<FreezeTagPlugin> logger)
        {
            _logger = logger;
        }

        [EventListener]
        public async ValueTask OnGameStarting(IGameStartingEvent e)
        {
            if (!DeactivatedGames.Contains(e.Game))
            {
                e.Game.Options.KillCooldown = int.MaxValue;
                e.Game.Options.NumEmergencyMeetings = 0;
                await e.Game.SyncSettingsAsync();
            }

        }

        [EventListener]
        public async ValueTask OnGameStarted(IGameStartedEvent e)
        {
            if (!DeactivatedGames.Contains(e.Game))
            {
                List<IClientPlayer> impostors = new List<IClientPlayer>();
                ConcurrentDictionary<IClientPlayer, Vector2> frozen = new ConcurrentDictionary<IClientPlayer, Vector2>();

                foreach (var player in e.Game.Players)
                {
                    if (player.Character.PlayerInfo.IsImpostor)
                    {
                        await player.Character.SetColorAsync(ColorType.Red);
                        impostors.Add(player);
                    }
                    else
                    {
                        await player.Character.SetColorAsync(ColorType.Green);
                    }
                }
                CodeAndInfos.Add(e.Game, new FreezeTagInfos(impostors, frozen));
            }
        }

        [EventListener]
        public async ValueTask OnPlayerMovement(IPlayerMovementEvent e)
        {
            if (CodeAndInfos.ContainsKey(e.Game))
            {
                List<IClientPlayer> impostors = CodeAndInfos[e.Game].impostors;
                ConcurrentDictionary<IClientPlayer, Vector2> frozens = CodeAndInfos[e.Game].frozens;
                IEnumerable<IClientPlayer> crewmates = e.Game.Players.Except(impostors).Except(frozens.Keys);

                if (frozens.ContainsKey(e.ClientPlayer)) {
                    if (frozens.TryGetValue(e.ClientPlayer, out var position))
                    {
                        await e.ClientPlayer.Character.NetworkTransform.SnapToAsync(position);
                    }
                    else {
                        _logger.LogWarning($"[FTag] Could not read position from ConcurrentDictionary for frozen player: {e.ClientPlayer.Character.PlayerInfo.PlayerName} in {e.Game.Code}");
                    }
                    return;
                }

                if (crewmates.Contains(e.ClientPlayer)) {
                    var sun = e.ClientPlayer;
                    foreach (var pair in frozens)
                    {
                        IClientPlayer frozen = pair.Key;
                        Vector2 position = pair.Value;
                        if (sun != frozen && CheckIfColliding(sun, frozen))
                        {
                            await Unfreeze(frozen).ConfigureAwait(true);
                            frozens.Remove(frozen, out _);
                        }
                    }
                }

                if (impostors.Contains(e.ClientPlayer)) {
                    var impostor = e.ClientPlayer;
                    foreach (var crewmate in crewmates)
                    {
                        if (CheckIfColliding(crewmate, impostor))
                        {
                            frozens.TryAdd(crewmate, crewmate.Character.NetworkTransform.Position);
                            await crewmate.Character.SetColorAsync(ColorType.Blue);
                            if (!crewmates.Any())
                            {
                                foreach (var nonImpostor in e.Game.Players.Except(impostors))
                                {
                                    await nonImpostor.KickAsync();
                                }
                            }
                        }
                    }
                }
            }
        }

        private bool CheckIfColliding(IClientPlayer player1, IClientPlayer player2)
        {
            Vector2 crewmatePos = player1.Character.NetworkTransform.Position;
            Vector2 impostorPos = player2.Character.NetworkTransform.Position;
            float crewmateX = (float)Math.Round(crewmatePos.X, 1);
            float crewmateY = (float)Math.Round(crewmatePos.Y, 1);
            float impostorX = (float)Math.Round(impostorPos.X, 1);
            float impostorY = (float)Math.Round(impostorPos.Y, 1);

            return crewmateX <= impostorX + freezeRange && crewmateX >= impostorX - freezeRange
                && crewmateY <= impostorY + freezeRange && crewmateY >= impostorY - freezeRange;
        }

        private async ValueTask Unfreeze(IClientPlayer frozen)
        {
            Thread.Sleep(2500);
            await frozen.Character.SetColorAsync(ColorType.Green);
        }

        [EventListener]
        public void OnGameEnded(IGameEndedEvent e)
        {
            if (CodeAndInfos.ContainsKey(e.Game))
            {
                CodeAndInfos[e.Game].frozens.Clear();
                CodeAndInfos[e.Game].impostors.Clear();
                CodeAndInfos.Remove(e.Game);
            }
        }

        [EventListener]
        public async ValueTask OnPlayerDeath(IPlayerMurderEvent e)
        {
            if (CodeAndInfos.ContainsKey(e.Game))
            {
                foreach (var impostor in CodeAndInfos[e.Game].impostors)
                {
                    await impostor.KickAsync();
                }
            }
        }

        [EventListener]
        public async ValueTask OnPlayerChat(IPlayerChatEvent e)
        {
            if (e.Game.GameState != GameStates.NotStarted || !e.Message.StartsWith(commandPrefix))
            {
                return;
            }

            switch (e.Message.ToLowerInvariant()[($"{commandPrefix} ".Length)..])
            {
                case "on":
                    if (!e.ClientPlayer.IsHost)
                    {
                        await ServerSendChatToPlayerAsync("[FF0000FF]You can't enable Freeze Tag because you aren't the host.", e.ClientPlayer.Character);
                        break;
                    }

                    if (DeactivatedGames.Contains(e.Game))
                    {
                        DeactivatedGames.Remove(e.Game);
                        await ServerSendChatAsync("[00FF00FF]Freeze Tag activated for this game.", e.ClientPlayer.Character);
                        await ServerSendChatAsync(gameModeExplanation, e.ClientPlayer.Character);
                        break;
                    } 

                    await ServerSendChatAsync("[FFA500FF]Freeze Tag was already active.", e.ClientPlayer.Character);                    
                    break;
                case "off":
                    if (!e.ClientPlayer.IsHost)
                    {
                        await ServerSendChatToPlayerAsync("[FF0000FF]You can't disable Freeze Tag because you aren't the host.", e.ClientPlayer.Character);
                        break;
                    }

                    if (!DeactivatedGames.Contains(e.Game))
                    {
                        DeactivatedGames.Add(e.Game);
                        await ServerSendChatAsync("[00FF00FF]Freeze Tag deactivated for this game.", e.ClientPlayer.Character);
                        break;
                    }

                    await ServerSendChatAsync("[FFA500FF]Freeze Tag was already off.", e.ClientPlayer.Character);    
                    break;
                case "":
                case "help":
                    await ServerSendChatToPlayerAsync(gameModeExplanation, e.ClientPlayer.Character);
                    break;
            }
        }

        private async ValueTask ServerSendChatAsync(string text, IInnerPlayerControl player)
        {
            string playername = player.PlayerInfo.PlayerName;
            await player.SetNameAsync($"FreezeTag");
            await player.SendChatAsync($"{text}");
            await player.SetNameAsync(playername);
        }

        private async ValueTask ServerSendChatToPlayerAsync(string text, IInnerPlayerControl player)
        {
            string playername = player.PlayerInfo.PlayerName;
            await player.SetNameAsync($"FreezeTagPrivate");
            await player.SendChatToPlayerAsync($"{text}");
            await player.SetNameAsync(playername);
        }
    }
}