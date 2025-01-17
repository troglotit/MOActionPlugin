﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Keys;
using MOAction.Configuration;
using Vector3 = System.Numerics.Vector3;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Game.Gui;
using FFXIVClientStructs.FFXIV.Client.Game;
using static MOAction.MOActionAddressResolver;
using Dalamud;

namespace MOAction
{
    public class MOAction
    {
        public delegate bool OnRequestActionDetour(long param_1, byte param_2, ulong param_3, long param_4,
                       uint param_5, uint param_6, uint param_7, long param_8);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate ulong ResolvePlaceholderActor(long param1, string param2, byte param3, byte param4);

        private delegate void PostRequest(IntPtr param1, long param2);
        private PostRequest PostRequestResolver;

        public delegate void OnSetUiMouseoverEntityId(long param1, long param2);

        private delegate IntPtr GetPronounResolver(IntPtr UiModuleSelf);
        private GetPronounResolver getPronounResolver;

        private readonly MOActionAddressResolver Address;
        private MOActionConfiguration Configuration;

        private Hook<OnRequestActionDetour> requestActionHook;
        private Hook<OnSetUiMouseoverEntityId> uiMoEntityIdHook;

        public unsafe delegate RecastTimer* GetGroupTimerDelegate(void* @this, int cooldownGroup);
        private readonly GetGroupTimerDelegate getGroupTimer;

        public List<MoActionStack> Stacks { get; set; }
        private DalamudPluginInterface pluginInterface;
        private IEnumerable<Lumina.Excel.GeneratedSheets.Action> RawActions;

        public IntPtr fieldMOLocation;
        public IntPtr focusTargLocation;
        public IntPtr regularTargLocation;
        public IntPtr uiMoEntityId = IntPtr.Zero;

        private HashSet<uint> UnorthodoxFriendly;
        private HashSet<uint> UnorthodoxHostile;

        public HashSet<ulong> enabledActions;

        public bool IsGuiMOEnabled = false;
        public bool IsFieldMOEnabled = false;

        public DataManager dataManager;
        public TargetManager targetManager;
        public ClientState clientState;
        public KeyState keyState;
        public static ObjectTable objectTable;
        private GameGui gameGui;

        private unsafe PronounModule* PM;
        private unsafe ActionManager* AM;
        private readonly int IdOffset = (int)Marshal.OffsetOf<FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject>("ObjectID");

        public MOAction(SigScanner scanner, ClientState clientstate,
                        DataManager datamanager, TargetManager targetmanager, ObjectTable objects, KeyState keystate, GameGui gamegui
                        )
        {
            Address = new();
            Address.Setup(scanner);
            clientstate.Login += LoadClientModules;
            clientstate.Logout += ClearClientModules;
            if (clientstate.IsLoggedIn)
                LoadClientModules(null, null);

            fieldMOLocation = Address.FieldMO;
            focusTargLocation = Address.FocusTarg;
            regularTargLocation = Address.RegularTarg;
            
            dataManager = datamanager;

            RawActions = dataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.Action>();

            targetManager = targetmanager;
            clientState = clientstate;
            objectTable = objects;
            keyState = keystate;
            gameGui = gamegui;

            getGroupTimer = Marshal.GetDelegateForFunctionPointer<GetGroupTimerDelegate>(Address.GetGroupTimer);

            Stacks = new();

            PluginLog.Log("===== M O A C T I O N =====");
            PluginLog.Log("RequestAction address {IsIconReplaceable}", Address.RequestAction);
            PluginLog.Log("SetUiMouseoverEntityId address {SetUiMouseoverEntityId}", Address.SetUiMouseoverEntityId);
            
            uiMoEntityIdHook = new Hook<OnSetUiMouseoverEntityId>(Address.SetUiMouseoverEntityId, new OnSetUiMouseoverEntityId(HandleUiMoEntityId));

            enabledActions = new();
            UnorthodoxFriendly = new();
            UnorthodoxHostile = new();
            UnorthodoxHostile.Add(3575);
            UnorthodoxFriendly.Add(17055);
            UnorthodoxFriendly.Add(7443);
        }

        public void SetConfig(MOActionConfiguration config)
        {
            Configuration = config;
        }

        private unsafe void LoadClientModules(object sender, EventArgs args)
        {
            try
            {
                var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
                var uiModule = framework->GetUiModule();
                PM = uiModule->GetPronounModule();
                AM = ActionManager.Instance();
            }
            catch (Exception e) {
                PluginLog.Log(e.Message);
                PluginLog.Log(e.StackTrace);
                PluginLog.Log(e.InnerException.ToString());
            }
        }

        private unsafe void ClearClientModules(object sender, EventArgs args)
        {
            PM = null;
            AM = null;
        }

        private unsafe void HookUseAction()
        {
            SafeMemory.WriteBytes(Address.GtQueuePatch, new byte[] { 0xEB });
            requestActionHook = new Hook<OnRequestActionDetour>((IntPtr)ActionManager.fpUseAction, new OnRequestActionDetour(HandleRequestAction));
            requestActionHook.Enable();
        }

        public void Enable()
        {
            uiMoEntityIdHook.Enable();

            HookUseAction();
        }

        public void Dispose()
        {
            if (requestActionHook.IsEnabled)
            {
                requestActionHook.Dispose();
                uiMoEntityIdHook.Dispose();
                
                SafeMemory.WriteBytes(Address.GtQueuePatch, new byte[] { 0x74 });
            }
        }

        public unsafe RecastTimer* GetGroupRecastTimer(int group)
        {
            return group < 1 ? null : getGroupTimer(AM, group - 1);
        }

        private void HandleUiMoEntityId(long param1, long param2)
        {
            uiMoEntityId = (IntPtr)param2;
            uiMoEntityIdHook.Original(param1, param2);
        }

        private unsafe bool HandleRequestAction(long param_1, byte actionType, ulong actionID, long param_4,
                       uint param_5, uint param_6, uint param_7, long param_8)
        {
            // Only care about "real" actions. Not doing anything dodgy, except for GT.
            if (actionType != 1)
            {
                return requestActionHook.Original(param_1, actionType, actionID, param_4, param_5, param_6, param_7, param_8);
            }
            var (action, target) = GetActionTarget((uint)actionID, actionType);
            
            if (action == null) return requestActionHook.Original(param_1, actionType, actionID, param_4, param_5, param_6, param_7, param_8);

            // Earthly Star is the only GT that changes to a different action.
            if (action.Name == "Earthly Star" && clientState.LocalPlayer.StatusList.Any(x => x.StatusId == 1248 || x.StatusId == 1224))
                return requestActionHook.Original(param_1, actionType, actionID, param_4, param_5, param_6, param_7, param_8);
            

            long objectId = target == null ? 0xE0000000 : target.ObjectId;

            bool ret = requestActionHook.Original(param_1, actionType, action.RowId, objectId, param_5, param_6, param_7, param_8);

            // Enqueue GT action
            if (action.TargetArea)
            {

                *(long*)((IntPtr)AM + 0x98) = objectId;
                *(byte*)((IntPtr)AM + 0xB8) = 1;
            }
            return ret;
        }

        private (Lumina.Excel.GeneratedSheets.Action action, GameObject target) GetActionTarget(uint ActionID, uint ActionType)
        {
            var action = RawActions.FirstOrDefault(x => x.RowId == ActionID);
            if (action == default) return (null, null);
            var applicableActions = Stacks.Where(entry => entry.BaseAction == action);
            MoActionStack stackToUse = null;
            foreach (var entry in applicableActions)
            {
                if (entry.Modifier == VirtualKey.NO_KEY)
                {
                    stackToUse = entry;
                }
                else if (keyState[entry.Modifier])
                {
                    stackToUse = entry;
                }
            }
            if (stackToUse == null)
            {
                return (null, null);
            }
            foreach (StackEntry entry in stackToUse.Entries)
            {
                if (CanUseAction(entry, ActionType))
                {
                    if (!entry.Action.CanTargetFriendly && !entry.Action.CanTargetHostile && !entry.Action.CanTargetParty && !entry.Action.CanTargetDead) return (entry.Action, clientState.LocalPlayer);
                    return (entry.Action, entry.Target.getPtr());
                }
            }
            return (null, null);
        }

        private unsafe int AvailableCharges(Lumina.Excel.GeneratedSheets.Action action)
        {
            RecastTimer* timer;
            if (action.CooldownGroup == 58)
                timer = GetGroupRecastTimer(action.AdditionalCooldownGroup);
            else
                timer = GetGroupRecastTimer(action.CooldownGroup);
            if (action.MaxCharges == 0) return timer->IsActive ^ 1;
            return (int)((action.MaxCharges+1) * (timer->Elapsed / timer->Total));
        }

        private unsafe bool CanUseAction(StackEntry targ, uint actionType)
        {
            if (targ.Target == null || targ.Action == null) return false;
            
            var action = targ.Action;
            var action2 = AM->GetAdjustedActionId(action.RowId);
            action = RawActions.First(x => x.RowId == action2);
            var target = targ.Target.GetTargetActorId();

            // ground target "at my mouse cursor"
            if (!targ.Target.ObjectNeeded)
            {
                return true;
            }
            
            foreach (GameObject a in objectTable)            
            {
                if (a != null && a.ObjectId == target)
                {
                    // Check if ability is on CD or not (charges are fun!)
                    unsafe
                    {
                        if (action.ActionCategory.Value.RowId == (uint)ActionType.Ability && action.MaxCharges == 0)
                        {
                            if (AM->IsRecastTimerActive((ActionType)actionType, action.RowId))
                            {
                                return false;
                            }
                        }
                        else if (action.MaxCharges > 0 || (action.CooldownGroup != 0 && action.AdditionalCooldownGroup != 0))
                        {
                            if (AvailableCharges(action) == 0) return false;
                            
                        }
                    }
                    if (Configuration.RangeCheck)
                    {
                        if (UnorthodoxFriendly.Contains((uint)action.RowId))
                        {
                            if (a.YalmDistanceX > 30) return false;
                        }
                        else if ((byte)action.Range < a.YalmDistanceX) return false;
                    }
                    if (a.ObjectKind == ObjectKind.Player) return action.CanTargetFriendly || action.CanTargetParty 
                            || action.CanTargetSelf
                            || action.RowId == 17055 || action.RowId == 7443;
                    if (a.ObjectKind == ObjectKind.BattleNpc)
                    {
                        BattleNpc b = (BattleNpc)a;
                        if (b.BattleNpcKind != BattleNpcSubKind.Enemy) return action.CanTargetFriendly || action.CanTargetParty
                                || action.CanTargetSelf
                                || UnorthodoxFriendly.Contains((uint)action.RowId);
                    }
                    return action.CanTargetHostile || UnorthodoxHostile.Contains((uint)action.RowId);
                }
            }
            return false;
        }

        public GameObject GetGuiMoPtr()
        {
            return objectTable.CreateObjectReference(uiMoEntityId);
        }
        public uint GetFieldMoPtr() => (uint)Marshal.ReadInt32(fieldMOLocation);
        public GameObject GetFocusPtr()
        {
            return objectTable.CreateObjectReference(Marshal.ReadIntPtr(focusTargLocation));
        }
        public GameObject GetRegTargPtr()
        {
            return objectTable.CreateObjectReference(regularTargLocation - IdOffset);
        }
        public GameObject NewFieldMo() => targetManager.MouseOverTarget;

        public unsafe GameObject GetActorFromPlaceholder(string placeholder)
        {
            return objectTable.CreateObjectReference((IntPtr)PM->ResolvePlaceholder(placeholder, 1, 0));
        }
    }
}