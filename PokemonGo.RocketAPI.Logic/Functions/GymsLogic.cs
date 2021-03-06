﻿/*
 * Created by SharpDevelop.
 * User: Xelwon
 * Date: 28/01/2017
 * Time: 18:45
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */
using System;
using POGOProtos.Settings.Master;
using PokemonGo.RocketAPI.Logic.Shared;

using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using GoogleMapsApi;
using GoogleMapsApi.Entities.Common;
using GoogleMapsApi.Entities.Directions.Request;
using GoogleMapsApi.Entities.Directions.Response;
using PokemonGo.RocketApi.PokeMap;
using PokemonGo.RocketAPI.Exceptions;
using PokemonGo.RocketAPI.Helpers;
using PokemonGo.RocketAPI.Logic.Utils;
using POGOProtos.Enums;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Fort;
using POGOProtos.Networking.Responses;
using Telegram.Bot;
using PokemonGo.RocketAPI;
using PokemonGo.RocketAPI.Logic;
using PokemonGo.RocketApi.PokeMap.DataModel;
using System.IO;
using System.Text;
using POGOProtos.Map.Pokemon;
using PokemonGo.RocketAPI.Logic.Functions;
using System.Threading.Tasks;
using PokemonGo.RocketAPI.Logic.Shared;
using PokemonGo.RocketAPI.HttpClient;
using System.Net.Http.Headers;
using System.Net.Http;
using POGOProtos.Data;
using POGOProtos.Data.Battle;

namespace PokemonGo.RocketAPI.Logic.Functions
{
    /// <summary>
    /// Description of GymsLogic.
    /// </summary>
    public static class GymsLogic
    {
        private static List<string> gymsVisited = new List<string>();
        private static int  GetGymLevel(long value)
        {
            if (value >= 50000)
                return 10;
            if (value >= 40000)
                return 9;
            if (value >= 30000)
                return 8;
            if (value >= 20000)
                return 7;
            if (value >= 16000)
                return 6;
            if (value >= 12000)
                return 5;
            if (value >= 8000)
                return 4;
            if (value >= 4000)
                return 3;
            if (value >= 2000)
                return 2;
            return 1;
        }
        public static void Execute()
        {
            if (!GlobalVars.FarmGyms)
                return;
            //narrow map data to gyms within walking distance
            var gyms = GetNearbyGyms();
            var gymsWithinRangeStanding = gyms.Where(i => LocationUtils.CalculateDistanceInMeters(Logic.objClient.CurrentLatitude, Logic.objClient.CurrentLongitude, i.Latitude, i.Longitude) < 40);

            var withinRangeStandingList = gymsWithinRangeStanding as IList<FortData> ?? gymsWithinRangeStanding.ToList();
            var inRange = withinRangeStandingList.Count;
            if (withinRangeStandingList.Any()) {
                Logger.ColoredConsoleWrite(ConsoleColor.DarkGray, $"(Gym) - {inRange} gyms are within range of the user");

                foreach (var gym in withinRangeStandingList) {
                    var fortInfo = Logic.objClient.Fort.GetFort(gym.Id, gym.Latitude, gym.Longitude).Result;
                    CheckAndPutInNearbyGym(gym, Logic.objClient, fortInfo);
                    Setout.SetCheckTimeToRun();
                    RandomHelper.RandomSleep(100, 200);
                }
            }

        }
        
        private static FortData[] GetNearbyGyms(GetMapObjectsResponse mapObjectsResponse = null)
        {
            if (mapObjectsResponse == null)
                mapObjectsResponse = Logic.objClient.Map.GetMapObjects().Result.Item1;

            var pokeGyms = Logic.Instance.navigation
                .pathByNearestNeighbour(
                               mapObjectsResponse.MapCells.SelectMany(i => i.Forts)
                    .Where(i => i.Type == FortType.Gym)
                    .OrderBy(i => LocationUtils.CalculateDistanceInMeters(Logic.objClient.CurrentLatitude, Logic.objClient.CurrentLongitude, i.Latitude, i.Longitude))
                    .ToArray(), GlobalVars.WalkingSpeedInKilometerPerHour);

            return pokeGyms;
        }

        private static bool CheckAndPutInNearbyGym(FortData gym, Client client, FortDetailsResponse fortInfo)
        {
            var gymColorLog = ConsoleColor.DarkGray;

            if (gymsVisited.IndexOf(gym.Id) > -1) {
                Logger.ColoredConsoleWrite(gymColorLog, "(Gym) - This gym was already visited.");
                return false;
            }
            if (GlobalVars.FarmGyms) {
                var pokemons = (client.Inventory.GetPokemons().Result).ToList();

                PokemonData pokemon = getPokeToPut(client);
                
                if (pokemon == null) {
                    Logger.ColoredConsoleWrite(gymColorLog, "(Gym) - There are no pokemons to assign.");
                    return false;
                }
                RandomHelper.RandomSleep(100, 200);
                var profile = client.Player.GetPlayer().Result;
                if ((gym.OwnedByTeam == profile.PlayerData.Team) || (gym.OwnedByTeam == POGOProtos.Enums.TeamColor.Neutral)) {
                    RandomHelper.RandomSleep(100, 200);
                    var gymDetails = client.Fort.GetGymDetails(gym.Id, gym.Latitude, gym.Longitude).Result;
                    Logger.ColoredConsoleWrite(gymColorLog, "Members: " + gymDetails.GymState.Memberships.Count + ". Level: " + GetGymLevel(gym.GymPoints));
                    if (gymDetails.GymState.Memberships.Count < GetGymLevel(gym.GymPoints)) {
                        putInGym(client,gym,pokemon,pokemons);
                    } else {
                        Logger.ColoredConsoleWrite(gymColorLog, "(Gym) - There is no free space in the gym");
                    }
                } else {
                    Logger.ColoredConsoleWrite(gymColorLog, "(Gym) - This gym is not your team.");
                    if (!GlobalVars.AttackGyms)
                        return false;
                    var gymDetails = client.Fort.GetGymDetails(gym.Id, gym.Latitude, gym.Longitude).Result;
                    // TO-DO ATTACK more than 1 defender
                    if (gymDetails.GymState.Memberships.Count == 1) {
                        Logger.ColoredConsoleWrite(gymColorLog, "There is only one rival. Let's go to fight");
                        var pokeAttackers = pokemons.Where(x => ((!x.IsEgg) && (x.DeployedFortId == "") && (x.Stamina > 0))).OrderBy(x => x.Cp).Take(6);
                        var pokeAttackersIds = pokeAttackers.Select(x => x.Id);
                        var defenders = gymDetails.GymState.Memberships.Select(x => x.PokemonData);
                        RandomHelper.RandomSleep(1000, 1500);
                        var moveSettings = GetMoveSettings(client);
                        foreach (var defender in defenders) {
                            var resp = client.Fort.StartGymBattle(gym.Id, defender.Id, pokeAttackersIds).Result;
                            if (resp.BattleLog.State == POGOProtos.Data.Battle.BattleState.Active) {
                                Logger.ColoredConsoleWrite(gymColorLog, "Battle Started");
                                RandomHelper.RandomSleep(2500, 3000);
                               
                                var battleActions = new List<BattleAction>();
                                var lastRetrievedAction = new BattleAction();
                                var battleStartMs = resp.BattleLog.BattleStartTimestampMs;
                                var attResp = client.Fort.AttackGym(gym.Id, resp.BattleId, battleActions, lastRetrievedAction).Result;
                                
                                var inBattle = (attResp.Result == AttackGymResponse.Types.Result.Success);
                                inBattle = (attResp.BattleLog.State == BattleState.Active);
                                var count = 1;
                                while (inBattle) {
                                    var timeMs = attResp.BattleLog.ServerMs;
                                    var move1Settings = moveSettings.FirstOrDefault(x => x.MoveSettings.MovementId == attResp.ActiveAttacker.PokemonData.Move1).MoveSettings;
                                    var attack = new BattleAction();
                                    attack.Type = BattleActionType.ActionAttack;
                                    attack.DurationMs = move1Settings.DurationMs; 
                                    attack.DamageWindowsStartTimestampMs = move1Settings.DamageWindowStartMs;
                                    attack.DamageWindowsEndTimestampMs = move1Settings.DamageWindowEndMs;
                                    attack.ActionStartMs = timeMs + move1Settings.DurationMs;
                                    attack.TargetIndex = -1;
                                    attack.ActivePokemonId = attResp.ActiveAttacker.PokemonData.Id;
                                    attack.TargetPokemonId = attResp.ActiveDefender.PokemonData.Id;
                                    battleActions.Clear();
                                    battleActions.Add(attack);
                                    lastRetrievedAction = attResp.BattleLog.BattleActions.LastOrDefault();
                                    attResp = client.Fort.AttackGym(gym.Id, resp.BattleId, battleActions, lastRetrievedAction).Result;
                                    Logger.ColoredConsoleWrite(gymColorLog, $"Attack {count} done.");
                                    inBattle = (attResp.Result == AttackGymResponse.Types.Result.Success);
                                    inBattle = inBattle && (attResp.BattleLog.State == BattleState.Active);
                                    count++;
                                    Logger.ColoredConsoleWrite(gymColorLog, "Wait a moment before next attact");
                                    RandomHelper.RandomSleep(move1Settings.DurationMs + 200, move1Settings.DurationMs + 300);
                                }
                                if (attResp.Result == AttackGymResponse.Types.Result.Success) {
                                    if (attResp.BattleLog.State == BattleState.Defeated)
                                        Logger.ColoredConsoleWrite(gymColorLog, "We have lost");
                                    else if (attResp.BattleLog.State == BattleState.Victory) {
                                        Logger.ColoredConsoleWrite(gymColorLog, "We have won");
                                        putInGym(client,gym,  getPokeToPut(client),pokemons);
                                    } else if (attResp.BattleLog.State == BattleState.TimedOut)
                                        Logger.ColoredConsoleWrite(gymColorLog, "Timed Out");
                                    gymsVisited.Add(gym.Id);
                                }

                            }
                        }
                    }
                    
                }
            }
            return true;
        }
        private static PokemonData getPokeToPut(Client client)
        {
            var pokemons = (client.Inventory.GetPokemons().Result).ToList();

            PokemonData pokemon;

            if (GlobalVars.LeaveInGyms == 0) {
                var rnd = new Random();
                pokemon = pokemons.Where(x => ((!x.IsEgg) && (x.DeployedFortId == "") && (x.Stamina == x.StaminaMax))).OrderBy(x => rnd.Next()).FirstOrDefault();
            } else if (GlobalVars.LeaveInGyms == 1)
                pokemon = pokemons.Where(x => ((!x.IsEgg) && (x.DeployedFortId == "")&& (x.Stamina == x.StaminaMax))).OrderByDescending(x => x.Cp).FirstOrDefault();
            else
                pokemon = pokemons.Where(x => ((!x.IsEgg) && (x.DeployedFortId == "")&& (x.Stamina == x.StaminaMax))).OrderBy(x => x.Cp).FirstOrDefault();
            return pokemon;
        }

        private static void putInGym(Client client, FortData gym, PokemonData pokemon, IEnumerable<PokemonData> pokemons)
        {
            RandomHelper.RandomSleep(100, 200);
            var fortSearch = client.Fort.FortDeployPokemon(gym.Id, pokemon.Id).Result;
            if (fortSearch.Result.ToString().ToLower() == "success") {
                Logger.ColoredConsoleWrite(ConsoleColor.DarkGray, StringUtils.getPokemonNameByLanguage((PokemonId)pokemon.PokemonId) + " inserted into the gym");
                gymsVisited.Add(gym.Id);
                var pokesInGym = pokemons.Where(x => ((!x.IsEgg) && (x.DeployedFortId != ""))).OrderBy(x => x.Cp).ToList().Count();
                Logger.ColoredConsoleWrite(ConsoleColor.DarkGray, "pokesInGym: " + pokesInGym);
                if (pokesInGym > 9) { 
                    var res = client.Player.CollectDailyDefenderBonus().Result;
                    Logger.ColoredConsoleWrite(ConsoleColor.DarkGray, $"(Gym) - Collected: {res.CurrencyAwarded} Coins.");
                }
            }
        }
        

        private static IEnumerable<DownloadItemTemplatesResponse.Types.ItemTemplate> GetMoveSettings(Client client)
        {
            var templates = client.Download.GetItemTemplates().Result.ItemTemplates;
            return templates.Where(x => x.MoveSettings != null);
            // && x.MoveSettings.MovementId == move
        }
    }
}
