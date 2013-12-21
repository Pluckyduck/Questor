﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DirectEve;
using Questor.Modules.Caching;
using Questor.Modules.Logging;
using Questor.Modules.Lookup;
using Questor.Modules.Activities;
using Questor.Modules.States;
using Questor.Modules.Combat;
using Questor.Modules.Actions;
using Questor.Modules.BackgroundTasks;
using Questor.Storylines;

namespace Questor.Behaviors
{
    public class MiningBehavior
    {
        private static readonly List<int> MiningToolGroupIDs = new List<int>();
        private readonly List<long> EmptyBelts = new List<long>();
        //private readonly Arm _arm;
        private readonly Panic _panic;
        //private readonly Combat _combat;
        //private readonly Drones _drones;
        private readonly UnloadLoot _unloadLoot;

        private bool PanicStateReset = false;
        private bool _isJammed = false;
        private int _minerNumber = 0;
        private double _lastAsteroidPosition = 0;
        private EntityCache _targetAsteroid;
        private long _targetAsteroidID;
        private EntityCache _currentBelt;
        private long _asteroidBookmarkForID = 0;

        private DateTime _lastModuleActivation = DateTime.MinValue;
        private DateTime _lastPulse;
        private DateTime _lastApproachCommand = DateTime.MinValue;

        public MiningBehavior()
        {
            //_arm = new Arm();
            _panic = new Panic();
            //_combat = new Combat();
            _unloadLoot = new UnloadLoot();
            //_drones = new Drones();

            _lastPulse = DateTime.MinValue;

            MiningToolGroupIDs.Add((int)Group.Miners);              //miners
            MiningToolGroupIDs.Add((int)Group.StripMiners);         //strip miners
            MiningToolGroupIDs.Add((int)Group.ModulatedStripMiners);//modulated strip miners
        }

        private void BeginClosingQuestor()
        {
            Cache.Instance.EnteredCloseQuestor_DateTime = DateTime.UtcNow;
            _States.CurrentQuestorState = QuestorState.CloseQuestor;
        }

        public void ProcessState()
        {
            if (Cache.Instance.SessionState == "Quitting")
            {
                BeginClosingQuestor();
            }

            if (Cache.Instance.GotoBaseNow)
            {
                _States.CurrentMiningState = MiningState.GotoBase;
            }

            if ((DateTime.UtcNow.Subtract(Cache.Instance.QuestorStarted_DateTime).TotalSeconds > 10)
                && (DateTime.UtcNow.Subtract(Cache.Instance.QuestorStarted_DateTime).TotalSeconds < 60))
            {
                if (Cache.Instance.QuestorJustStarted)
                {
                    Cache.Instance.QuestorJustStarted = false;
                    Cache.Instance.SessionState = "Starting Up";

                    // write session log
                    Statistics.WriteSessionLogStarting();
                }
            }

            _panic.ProcessState();

            if (_States.CurrentPanicState == PanicState.Panic || _States.CurrentPanicState == PanicState.Panicking)
            {
                // If Panic is in panic state, questor is in panic States.CurrentCombatMissionBehaviorState :)
                _States.CurrentMiningState = MiningState.Panic;

                if (PanicStateReset)
                {
                    _States.CurrentPanicState = PanicState.Normal;
                    PanicStateReset = false;
                }
            }
            else if (_States.CurrentPanicState == PanicState.Resume)
            {
                // Reset panic state
                _States.CurrentPanicState = PanicState.Normal;
                _States.CurrentTravelerState = TravelerState.Idle;
                _States.CurrentMiningState = MiningState.GotoBelt;
            }

            if (Settings.Instance.DebugStates)
            {
                Logging.Log("MiningBehavior", "Pre-switch", Logging.White);
            }

            switch (_States.CurrentMiningState)
            {
                case MiningState.Default:
                case MiningState.Idle:
                    _States.CurrentMiningState = MiningState.Cleanup;
                    break;

                case MiningState.Cleanup:
                    if (Cache.Instance.LootAlreadyUnloaded == false)
                    {
                        _States.CurrentMiningState = MiningState.GotoBase;
                        break;
                    }

                    Cleanup.CheckEVEStatus();
                    _States.CurrentMiningState = MiningState.Arm;
                    break;

                case MiningState.GotoBase:
                    DirectBookmark miningHome = Cache.Instance.BookmarksByLabel("Mining Home").FirstOrDefault();

                    //Cache.Instance.DirectEve.Navigation.GetDestinationPath
                    Traveler.TravelToMiningHomeBookmark(miningHome, "Mining go to base");

                    if (_States.CurrentTravelerState == TravelerState.AtDestination) // || DateTime.UtcNow.Subtract(Cache.Instance.EnteredCloseQuestor_DateTime).TotalMinutes > 10)
                    {
                        if (Settings.Instance.DebugGotobase) Logging.Log("MiningBehavior", "GotoBase: We are at destination", Logging.White);
                        Cache.Instance.GotoBaseNow = false; //we are there - turn off the 'forced' gotobase
                        _States.CurrentMiningState = MiningState.UnloadLoot;
                        Traveler.Destination = null;
                    }
                    break;

                case MiningState.UnloadLoot:

                    //
                    // this state should never be reached in space. if we are in space and in this state we should switch to gotobase
                    //
                    if (Cache.Instance.InSpace)
                    {
                        Logging.Log(_States.CurrentCombatMissionBehaviorState.ToString(), "We are in space, how did we get set to this state while in space? Changing state to: GotoBase", Logging.White);
                        _States.CurrentMiningState = MiningState.GotoBase;
                    }

                    if (_States.CurrentUnloadLootState == UnloadLootState.Idle)
                    {
                        Logging.Log("MiningBehavior", "UnloadLoot: Begin", Logging.White);
                        _States.CurrentUnloadLootState = UnloadLootState.Begin;
                    }

                    _unloadLoot.ProcessState();

                    if (_States.CurrentUnloadLootState == UnloadLootState.Done)
                    {
                        Cache.Instance.LootAlreadyUnloaded = true;
                        _States.CurrentUnloadLootState = UnloadLootState.Idle;

                        if (_States.CurrentCombatState == CombatState.OutOfAmmo)
                        {
                            Logging.Log("MiningBehavior.UnloadLoot", "We are out of ammo", Logging.Orange);
                            _States.CurrentMiningState = MiningState.Idle;
                            return;
                        }

                        _States.CurrentMiningState = MiningState.Idle;
                        _States.CurrentQuestorState = QuestorState.Idle;
                        Logging.Log("MiningBehavior.Unloadloot", "CharacterMode: [" + Settings.Instance.CharacterMode + "], AfterMissionSalvaging: [" + Settings.Instance.AfterMissionSalvaging + "], MiningState: [" + _States.CurrentMiningState + "]", Logging.White);
                        return;
                    }
                    break;

                case MiningState.Start:
                    Cache.Instance.OpenWrecks = false;
                    _States.CurrentMiningState = MiningState.Arm;
                    DirectBookmark asteroidShortcut = Cache.Instance.BookmarksByLabel("Asteroid Location").FirstOrDefault();

                    if (asteroidShortcut != null)
                    {
                        asteroidShortcut.Delete();
                    }
                    break;

                case MiningState.Arm:

                    //
                    // this state should never be reached in space. if we are in space and in this state we should switch to gotobase
                    //
                    if (Cache.Instance.InSpace)
                    {
                        Logging.Log(_States.CurrentMiningState.ToString(), "We are in space, how did we get set to this state while in space? Changing state to: GotoBase", Logging.White);
                        _States.CurrentMiningState = MiningState.GotoBase;
                    }

                    if (_States.CurrentArmState == ArmState.Idle)
                    {
                        Logging.Log("Arm", "Begin", Logging.White);
                        _States.CurrentArmState = ArmState.Begin;

                        // Load ammo... this "fixes" the problem I experienced with not reloading after second arm phase. The quantity was getting set to 0.
                        Arm.AmmoToLoad.Clear();
                        Arm.AmmoToLoad.Add(Settings.Instance.Ammo.FirstOrDefault());

                        //FIXME: bad hack - this should be fixed differently / elsewhere
                        Ammo FirstAmmoToLoad = Arm.AmmoToLoad.FirstOrDefault();
                        if (FirstAmmoToLoad != null && FirstAmmoToLoad.Quantity == 0)
                        {
                            FirstAmmoToLoad.Quantity = 333;
                        }
                        
                    }

                    Arm.ProcessState();

                    if (Settings.Instance.DebugStates) Logging.Log("Arm.State", "is" + _States.CurrentArmState, Logging.White);

                    if (_States.CurrentArmState == ArmState.NotEnoughAmmo)
                    {
                        // we know we are connected if we were able to arm the ship - update the lastknownGoodConnectedTime
                        // we may be out of drones/ammo but disconnecting/reconnecting will not fix that so update the timestamp
                        Cache.Instance.LastKnownGoodConnectedTime = DateTime.UtcNow;
                        Cache.Instance.MyWalletBalance = Cache.Instance.DirectEve.Me.Wealth;
                        Logging.Log("Arm", "Armstate.NotEnoughAmmo", Logging.Orange);
                        _States.CurrentArmState = ArmState.Idle;
                        _States.CurrentMiningState = MiningState.Error;
                    }

                    if (_States.CurrentArmState == ArmState.NotEnoughDrones)
                    {
                        // we know we are connected if we were able to arm the ship - update the lastknownGoodConnectedTime
                        // we may be out of drones/ammo but disconnecting/reconnecting will not fix that so update the timestamp
                        Cache.Instance.LastKnownGoodConnectedTime = DateTime.UtcNow;
                        Cache.Instance.MyWalletBalance = Cache.Instance.DirectEve.Me.Wealth;
                        Logging.Log("Arm", "Armstate.NotEnoughDrones", Logging.Orange);
                        _States.CurrentArmState = ArmState.Idle;
                        _States.CurrentMiningState = MiningState.Error;
                    }

                    if (_States.CurrentArmState == ArmState.Done)
                    {
                        if (DateTime.UtcNow > Cache.Instance.LastInSpace.AddSeconds(45)) //do not try to leave the station until you have been docked for at least 45seconds! (this gives some overhead to load the station env + session change timer)
                        {
                            //we know we are connected if we were able to arm the ship - update the lastknownGoodConnectedTime
                            Cache.Instance.LastKnownGoodConnectedTime = DateTime.UtcNow;
                            Cache.Instance.MyWalletBalance = Cache.Instance.DirectEve.Me.Wealth;
                            _States.CurrentArmState = ArmState.Idle;
                            _States.CurrentDroneState = DroneState.WaitingForTargets;


                            //exit the station
                            Cache.Instance.DirectEve.ExecuteCommand(DirectCmd.CmdExitStation);

                            //set up a wait of 10 seconds so the undock can complete
                            _lastPulse = DateTime.UtcNow.AddSeconds(10);
                            _States.CurrentMiningState = MiningState.GotoBelt;
                        }
                    }
                    break;

                case MiningState.GotoBelt:
                    if (DateTime.UtcNow.Subtract(_lastPulse).TotalMilliseconds < Time.Instance.QuestorPulse_milliseconds * 2)
                    {
                        return;
                    }

                    if (Cache.Instance.InWarp || (!Cache.Instance.InSpace && !Cache.Instance.InStation))
                    {
                        return;
                    }

                    //
                    // this should goto a mining system bookmark (one of possibly many)
                    // then goto the 1st belt. This would allow for mining in systems without stations
                    //
                    Logging.Log("MiningBehavior", "Setting Destination to 1st Asteroid belt.", Logging.White);

                    DirectBookmark asteroidShortcutGTB = Cache.Instance.BookmarksByLabel("Asteroid Location").FirstOrDefault();

                    if (asteroidShortcutGTB != null)
                    {
                        if (Cache.Instance.EntityById(_currentBelt.Id).Distance < 65000)
                        {
                            _States.CurrentMiningState = MiningState.Mine;
                            Traveler.Destination = null;
                        }
                        else
                        {
                            asteroidShortcutGTB.WarpTo();
                            _lastPulse = DateTime.UtcNow;
                        }
                        break;
                    }
                    
                    IEnumerable<EntityCache> belts = Cache.Instance.Entities.Where(i => i.GroupId == (int)Group.AsteroidBelt && !i.Name.ToLower().Contains("ice") && !EmptyBelts.Contains(i.Id));
                    EntityCache belt = belts.OrderBy(x => x.Distance).FirstOrDefault();
                    _currentBelt = belt;

                    //Traveler.Destination = new MissionBookmarkDestination(belt);

                    if (belt != null)
                    {
                        if (belt.Distance < 35000)
                        {
                            _States.CurrentMiningState = MiningState.Mine;
                            Traveler.Destination = null;
                        }
                        else
                        {
                            belt.WarpTo();
                            _lastPulse = DateTime.UtcNow;
                        }
                        break;
                    }

                    _States.CurrentMiningState = MiningState.GotoBase;
                    Logging.Log("MiningBehavior", "Could not find a suitable Asteroid belt.", Logging.White);
                    Settings.Instance.AutoStart = false;
                    break;

                case MiningState.Mine:

                    IEnumerable <EntityCache> _asteroidsOnGrid = Cache.Instance.EntitiesOnGrid.Where(i =>i.Distance < (int)Distances.OnGridWithMe && i.CategoryId == (int)CategoryID.Asteroid  ).OrderBy(i => i.Distance);
                    IEnumerable<EntityCache> _asteroidsInRange = _asteroidsOnGrid.Where(i => i.Distance < 65000).ToList();
                    EntityCache asteroid = null;

                    if (asteroid == null && _asteroidsInRange.Any(i => i.GroupId == (int) Group.Kernite))
                    {
                        asteroid = _asteroidsInRange.Where(i => i.GroupId == (int)Group.Kernite).OrderBy(i => i.Distance).FirstOrDefault();
                    }

                    if (asteroid == null && _asteroidsInRange.Any(i => i.GroupId == (int)Group.Plagioclase))
                    {
                        asteroid = _asteroidsInRange.Where(i => i.GroupId == (int)Group.Plagioclase).OrderBy(i => i.Distance).FirstOrDefault();
                    }

                    if (asteroid == null && _asteroidsInRange.Any(i => i.GroupId == (int)Group.Pyroxeres))
                    {
                        asteroid = _asteroidsInRange.Where(i => i.GroupId == (int)Group.Pyroxeres).OrderBy(i => i.Distance).FirstOrDefault();
                    }

                    if (asteroid == null && _asteroidsInRange.Any(i => i.GroupId == (int)Group.Scordite))
                    {
                        asteroid = _asteroidsInRange.Where(i => i.GroupId == (int)Group.Scordite).OrderBy(i => i.Distance).FirstOrDefault();
                    }

                    if (asteroid == null && _asteroidsInRange.Any(i => i.GroupId == (int)Group.Veldspar))
                    {
                        asteroid = _asteroidsInRange.Where(i => i.GroupId == (int)Group.Veldspar).OrderBy(i => i.Distance).FirstOrDefault();
                    }

                    
                    if (asteroid == null)
                    {
                        EmptyBelts.Add(_currentBelt.Id);
                        DirectBookmark asteroidShortcutBM2 = Cache.Instance.BookmarksByLabel("Asteroid Location").FirstOrDefault();

                        if (asteroidShortcutBM2 != null)
                        {
                            asteroidShortcutBM2.Delete();
                        }

                        Logging.Log("MiningBehavior", "Could not find a suitable Asteroid to mine in this belt.", Logging.White);
                        _States.CurrentMiningState = MiningState.GotoBelt;
                        break;
                    }
                    
                    Logging.Log("Mining: [", asteroid.Name + "][" + Math.Round(asteroid.Distance/1000,0) + "k] GroupID [" + asteroid.GroupId + "]", Logging.White);
                    _targetAsteroidID = asteroid.Id;

                    if (DateTime.Now > _lastApproachCommand.AddSeconds(Cache.Instance.RandomNumber(3, 6)))
                    {
                        _lastApproachCommand = DateTime.UtcNow;
                        _targetAsteroid.Approach();
                        _States.CurrentMiningState = MiningState.MineAsteroid;    
                    }
                    break;

                case MiningState.MineAsteroid:
                    if (Cache.Instance.EntityById(_targetAsteroidID) == null)
                    {
                        //asteroid is depleted
                        _States.CurrentMiningState = MiningState.Mine;
                        return;
                    }
                    _targetAsteroid = Cache.Instance.EntityById(_targetAsteroidID);
                    Combat.ProcessState();

                    if (Settings.Instance.DebugStates)
                        Logging.Log("MiningBehavior:MineAsteroid", "Combat processed. Combat.State is: " + _States.CurrentCombatState.ToString(), Logging.White);

                    Drones.ProcessState();

                    if (Settings.Instance.DebugStates)
                        Logging.Log("Drones.State is", _States.CurrentDroneState.ToString(), Logging.White);

                    // If we are out of ammo, return to base, the mission will fail to complete and the bot will reload the ship
                    // and try the mission again
                    if (_States.CurrentCombatState == CombatState.OutOfAmmo)
                    {
                        Logging.Log("Combat", "Out of Ammo!", Logging.Orange);
                        _States.CurrentMiningState = MiningState.GotoBase;
                    }

                    if (DateTime.UtcNow.Subtract(_lastPulse).TotalMilliseconds < Time.Instance.QuestorPulse_milliseconds) //default: 1500ms
                        return;

                    _lastPulse = DateTime.UtcNow;

                    //check if we're full

                    if (Cache.Instance.CurrentShipsCargo == null) return;
                    //if (!Cache.Instance.OpenInventoryWindow("Salvage")) return;

                    //should I check Cache.Instance.ActiveDrones.Any() instead?
                    if (Cache.Instance.CurrentShipsCargo.IsValid
                        && (Cache.Instance.CurrentShipsCargo.UsedCapacity >= Cache.Instance.CurrentShipsCargo.Capacity * .9)
                        && Cache.Instance.CurrentShipsCargo.Capacity > 0
                        && _States.CurrentDroneState == DroneState.WaitingForTargets)
                    {
                        Logging.Log("Miner:MineAsteroid", "We are full, go to base to unload. Capacity is: " + Cache.Instance.CurrentShipsCargo.Capacity
                            + ", Used: " + Cache.Instance.CurrentShipsCargo.UsedCapacity, Logging.White);
                        _States.CurrentMiningState = MiningState.GotoBase;
                        break;
                    }
                    else if (Cache.Instance.CurrentShipsCargo.IsValid
                      && (Cache.Instance.CurrentShipsCargo.UsedCapacity >= Cache.Instance.CurrentShipsCargo.Capacity * .9)
                      && Cache.Instance.CurrentShipsCargo.Capacity > 0
                      && _States.CurrentDroneState != DroneState.WaitingForTargets)
                    {
                        Logging.Log("Miner:MineAsteroid", "We are full, but drones are busy. Drone state: " + _States.CurrentDroneState.ToString(), Logging.White);
                    }

                    if (Settings.Instance.DebugStates) Logging.Log("Miner:MineAsteroid", "Distance to Asteroid is: " + _targetAsteroid.Distance, Logging.White);

                    if (_targetAsteroid.Distance < 10000)
                    {
                        if (_targetAsteroid.Distance < 94000)
                        {
                            if (_asteroidBookmarkForID != _targetAsteroid.Id)
                            {
                                DirectBookmark asteroidShortcutBM = Cache.Instance.BookmarksByLabel("Asteroid Location").FirstOrDefault();

                                if (asteroidShortcutBM != null)
                                {
                                    asteroidShortcutBM.UpdateBookmark("Asteroid Location", "Mining Shortcut");
                                }
                                else
                                {
                                    Cache.Instance.DirectEve.BookmarkCurrentLocation("Asteroid Location", "Mining Shortcut", null);
                                }

                                _asteroidBookmarkForID = _targetAsteroid.Id;
                            }
                        }

                        if (Cache.Instance.Targeting.Contains(_targetAsteroid))
                        {
                            if (Settings.Instance.DebugStates) Logging.Log("Miner:MineAsteroid", "Targeting asteroid.", Logging.White);
                            return;
                            //wait
                        }
                        else if (Cache.Instance.Targets.Contains(_targetAsteroid))
                        {
                            if (Settings.Instance.DebugStates) Logging.Log("Miner:MineAsteroid", "Asteroid Targeted.", Logging.White);
                            //if(!_targetAsteroid.IsActiveTarget) _targetAsteroid.MakeActiveTarget();
                            List<ModuleCache> miningTools = Cache.Instance.Modules.Where(m => MiningToolGroupIDs.Contains(m.GroupId)).ToList();

                            _minerNumber = 0;
                            foreach (ModuleCache miningTool in miningTools)
                            {
                                if (miningTool.ActivatedTimeStamp.AddSeconds(3) > DateTime.UtcNow)
                                    continue;

                                _minerNumber++;

                                // Are we on the right target?
                                if (miningTool.IsActive)
                                {
                                    if (miningTool.TargetId != _targetAsteroid.Id)
                                    {
                                        miningTool.Click();
                                        return;
                                    }
                                    continue;
                                }

                                // Are we deactivating?
                                if (miningTool.IsDeactivating)
                                    continue;

                                //only activate one module per third of a second
                                if (_lastModuleActivation < DateTime.UtcNow.AddMilliseconds(-350))
                                {
                                    _lastModuleActivation = DateTime.UtcNow;
                                    Logging.Log("Mining", "Activating mining tool [" + _minerNumber + "] on [" + _targetAsteroid.Name + "][" + Cache.Instance.MaskedID(_targetAsteroid.Id) + "][" + Math.Round(_targetAsteroid.Distance / 1000, 0) + "k away]", Logging.Teal);

                                    miningTool.Activate(_targetAsteroid.Id);
                                    miningTool.ActivatedTimeStamp = DateTime.UtcNow;
                                }
                            }
                        } //mine
                        else
                        { //asteroid is not targeted

                            if (Settings.Instance.DebugStates) Logging.Log("Miner:MineAsteroid", "Asteroid not targeted.", Logging.White);
                            if (DateTime.UtcNow < Cache.Instance.NextTargetAction) //if we just did something wait a fraction of a second
                                return;

                            if (Cache.Instance.MaxLockedTargets == 0)
                            {
                                if (!_isJammed)
                                {
                                    Logging.Log("Mining", "We are jammed and can't target anything", Logging.Orange);
                                }

                                _isJammed = true;
                                return;
                            }

                            if (_isJammed)
                            {
                                // Clear targeting list as it does not apply
                                Cache.Instance.TargetingIDs.Clear();
                                Logging.Log("Mining", "We are no longer jammed, ReTargeting", Logging.Teal);
                            }
                            _isJammed = false;

                            _targetAsteroid.LockTarget("Mining.targetAsteroid");
                            Cache.Instance.NextTargetAction = DateTime.UtcNow.AddMilliseconds(Time.Instance.TargetDelay_milliseconds);
                        }
                    } //check 10K distance
                    else
                    {
                        double velocity = _lastAsteroidPosition - _targetAsteroid.Distance;
                        _lastAsteroidPosition = _targetAsteroid.Distance;

                        if (Settings.Instance.DebugStates)
                            Logging.Log("Miner:MineAsteroid", "Distance greater than 10K. Debug are we flying there? "
                                + "approaching.targetValue [" + (Cache.Instance.Approaching.TargetValue == null ? "null" : Cache.Instance.Approaching.TargetValue.ToString())
                                + "] _targetRoid.Id [" + _targetAsteroid.Id
                                + "] targeting me count [" + Combat.TargetingMe.Count
                                + "]", Logging.White);
                        //this isn't working because Cache.Instance.Approaching.TargetValue always seems to return null. This will negatively impact combat since it won't orbit. Might want to check CombatState instead.
                        if (//Cache.Instance.Approaching.TargetValue == null
                            //|| (Cache.Instance.Approaching.TargetValue != _targetAsteroid.Id && _combat.TargetingMe.Count == 0)
                            velocity <= 0 && !Cache.Instance.TargetedBy.Any()
                            )
                        {
                            if (_lastApproachCommand.AddSeconds(2) < DateTime.UtcNow)
                            {
                                _targetAsteroid.Approach();
                                _lastApproachCommand = DateTime.UtcNow;
                            }
                        }
                    }

                    break;
            } //ends MiningState switch
        }//ends ProcessState method
    }
}
