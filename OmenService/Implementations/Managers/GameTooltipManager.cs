using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService.Abstractions;
using RowStatus = Lumina.Excel.Sheets.Status;

namespace OmenTools.OmenService;

public unsafe class GameTooltipManager : OmenServiceBase<GameTooltipManager>
{
    #region 外部委托

    public delegate void ItemTooltipEventDelegate(ItemTooltipContext context);

    public delegate void ActionTooltipEventDelegate(ActionTooltipContext context);

    public delegate void ActionDetailTooltipEventDelegate(ActionDetailTooltipContext context);

    public delegate void TooltipShowEventDelegate(TooltipShowContext context);

    public delegate void ItemTooltipRuleDelegate(ItemTooltipContext context);

    public delegate void ActionTooltipRuleDelegate(ActionTooltipContext context);

    public delegate void TooltipShowRuleDelegate(TooltipShowContext context);

    #endregion

    #region 私有字段

    private static readonly CompSig GenerateItemTooltipSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B 42 ?? 4C 8B EA");

    private delegate void* GenerateItemTooltipDelegate
    (
        AtkUnitBase*     addon,
        NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData
    );

    private Hook<GenerateItemTooltipDelegate>? GenerateItemTooltipHook;

    private static readonly CompSig GenerateActionTooltipSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC ?? 48 8B 42 ?? 4C 8B FA");

    private delegate void* GenerateActionTooltipDelegate
    (
        AtkUnitBase*     addon,
        NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData
    );

    private Hook<GenerateActionTooltipDelegate>? GenerateActionTooltipHook;

    private static readonly CompSig HandleActionHoverSig = new
        ("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC ?? 45 33 E4 41 8B E9");

    private delegate void HandleActionHoverDelegate
    (
        AgentActionDetail* agent,
        DetailKind         detailKind,
        uint               actionID,
        int                flag,
        bool               isLovmActionDetail,
        int                a6,
        int                a7
    );

    private Hook<HandleActionHoverDelegate>? HandleActionHoverHook;

    private static readonly CompSig ShowTooltipSig = new("4C 89 4C 24 ?? 66 44 89 44 24");

    private delegate void ShowTooltipDelegate
    (
        AtkTooltipManager*                manager,
        AtkTooltipType                    type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* tooltipArgs,
        void*                             unkDelegate,
        byte                              unk7,
        byte                              unk8
    );

    private Hook<ShowTooltipDelegate>? ShowTooltipHook;

    private readonly ConcurrentDictionary<Type, ImmutableList<Delegate>>            eventsCollection = [];
    private readonly ConcurrentDictionary<TooltipRuleType, ImmutableList<TooltipRule>> rulesCollection = [];

    private readonly TooltipActionDetail hoveredActionDetail = new();
    private          ReadOnlySeString    weatherTooltipText;

    #endregion

    protected override void Init()
    {
        GenerateItemTooltipHook ??= GenerateItemTooltipSig.GetHook<GenerateItemTooltipDelegate>(GenerateItemTooltipDetour);
        GenerateItemTooltipHook.Enable();

        GenerateActionTooltipHook ??= GenerateActionTooltipSig.GetHook<GenerateActionTooltipDelegate>(GenerateActionTooltipDetour);
        GenerateActionTooltipHook.Enable();

        HandleActionHoverHook ??= HandleActionHoverSig.GetHook<HandleActionHoverDelegate>(HandleActionHoverDetour);
        HandleActionHoverHook.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ActionDetail", OnActionDetailRequestedUpdate);

        ShowTooltipHook ??= ShowTooltipSig.GetHook<ShowTooltipDelegate>(ShowTooltipDetour);
        ShowTooltipHook.Enable();
    }

    protected override void Uninit()
    {
        GenerateItemTooltipHook?.Dispose();
        GenerateItemTooltipHook = null;

        GenerateActionTooltipHook?.Dispose();
        GenerateActionTooltipHook = null;

        DService.Instance().AddonLifecycle.UnregisterListener(OnActionDetailRequestedUpdate);

        HandleActionHoverHook?.Dispose();
        HandleActionHoverHook = null;

        ShowTooltipHook?.Dispose();
        ShowTooltipHook = null;

        eventsCollection.Clear();
        rulesCollection.Clear();
    }

    #region 公共接口

    public ReadOnlySeString GetShowenWeatherTooltip() =>
        weatherTooltipText;

    #endregion

    #region 注册/注销接口

    private void RegisterEventGeneric<T>(T method, params T[] methods) where T : Delegate
    {
        var type = typeof(T);

        eventsCollection.AddOrUpdate
        (
            type,
            _ =>
            {
                var list = ImmutableList.Create<Delegate>(method);
                return methods.Length > 0 ? list.AddRange(methods) : list;
            },
            (_, currentList) =>
            {
                var newList = currentList.Add(method);
                return methods.Length > 0 ? newList.AddRange(methods) : newList;
            }
        );
    }

    private bool UnregisterEventGeneric<T>(params T[] methods) where T : Delegate
    {
        if (methods is not { Length: > 0 }) return false;

        var type = typeof(T);

        while (eventsCollection.TryGetValue(type, out var currentList))
        {
            var newList = currentList.RemoveRange(methods);

            if (newList == currentList)
                return false;

            if (newList.IsEmpty)
            {
                var kvp = new KeyValuePair<Type, ImmutableList<Delegate>>(type, currentList);
                if (((ICollection<KeyValuePair<Type, ImmutableList<Delegate>>>)eventsCollection).Remove(kvp))
                    return true;
            }
            else
            {
                if (eventsCollection.TryUpdate(type, newList, currentList))
                    return true;
            }
        }

        return false;
    }

    private TooltipRule RegisterRule(TooltipRuleType type, Delegate method)
    {
        var rule = new TooltipRule(type, method);

        rulesCollection.AddOrUpdate
        (
            type,
            _ => ImmutableList.Create(rule),
            (_, currentList) => currentList.Add(rule)
        );

        return rule;
    }

    public void RegItemTooltip(ItemTooltipEventDelegate method, params ItemTooltipEventDelegate[] methods) =>
        RegisterEventGeneric(method, methods);

    public void RegActionTooltip(ActionTooltipEventDelegate method, params ActionTooltipEventDelegate[] methods) =>
        RegisterEventGeneric(method, methods);

    public void RegActionDetailTooltip(ActionDetailTooltipEventDelegate method, params ActionDetailTooltipEventDelegate[] methods) =>
        RegisterEventGeneric(method, methods);

    public void RegTooltipShow(TooltipShowEventDelegate method, params TooltipShowEventDelegate[] methods) =>
        RegisterEventGeneric(method, methods);

    public TooltipRule RegItemRule(ItemTooltipRuleDelegate rule) =>
        RegisterRule(TooltipRuleType.Item, rule);

    public TooltipRule RegActionRule(ActionTooltipRuleDelegate rule) =>
        RegisterRule(TooltipRuleType.Action, rule);

    public TooltipRule RegTooltipShowRule(TooltipShowRuleDelegate rule) =>
        RegisterRule(TooltipRuleType.Show, rule);

    public bool Unreg(params TooltipRule[] rules)
    {
        if (rules is not { Length: > 0 }) return false;

        var success = true;

        foreach (var rule in rules)
        {
            if (!rulesCollection.TryGetValue(rule.Type, out var currentList))
            {
                success = false;
                continue;
            }

            while (true)
            {
                var newList = currentList.Remove(rule);

                if (newList == currentList)
                {
                    success = false;
                    break;
                }

                if (newList.IsEmpty)
                {
                    var kvp = new KeyValuePair<TooltipRuleType, ImmutableList<TooltipRule>>(rule.Type, currentList);
                    if (((ICollection<KeyValuePair<TooltipRuleType, ImmutableList<TooltipRule>>>)rulesCollection).Remove(kvp))
                        break;
                }
                else
                {
                    if (rulesCollection.TryUpdate(rule.Type, newList, currentList))
                        break;
                }

                if (!rulesCollection.TryGetValue(rule.Type, out currentList))
                {
                    success = false;
                    break;
                }
            }
        }

        return success;
    }

    public bool Unreg(params ItemTooltipEventDelegate[] methods) => UnregisterEventGeneric(methods);

    public bool Unreg(params ActionTooltipEventDelegate[] methods) => UnregisterEventGeneric(methods);

    public bool Unreg(params ActionDetailTooltipEventDelegate[] methods) => UnregisterEventGeneric(methods);

    public bool Unreg(params TooltipShowEventDelegate[] methods) => UnregisterEventGeneric(methods);

    #endregion

    #region 事件处理

    private void HandleActionHoverDetour
    (
        AgentActionDetail* agent,
        DetailKind         detailKind,
        uint               actionID,
        int                flag,
        bool               isLovmActionDetail,
        int                a6,
        int                a7
    )
    {
        hoveredActionDetail.Category           = detailKind;
        hoveredActionDetail.ID                 = actionID;
        hoveredActionDetail.Flag               = flag;
        hoveredActionDetail.IsLovmActionDetail = isLovmActionDetail;
        HandleActionHoverHook?.Original(agent, detailKind, actionID, flag, isLovmActionDetail, a6, a7);
    }

    private void* GenerateItemTooltipDetour(AtkUnitBase* addon, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        var itemID = AgentItemDetail.Instance()->ItemId;
        if (itemID < 2_000_000)
            itemID %= 500_000;

        var context = new ItemTooltipContext(itemID, addon, numberArrayData, stringArrayData);

        InvokeItemRules(context);
        InvokeItemEvents(context);

        return GenerateItemTooltipHook.Original(addon, numberArrayData, stringArrayData);
    }

    private void* GenerateActionTooltipDetour(AtkUnitBase* addon, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        var agent   = AgentActionDetail.Instance();
        var context = new ActionTooltipContext(agent->ActionId, agent->OriginalId, hoveredActionDetail, addon, numberArrayData, stringArrayData);

        InvokeActionRules(context);
        InvokeActionEvents(context);

        return GenerateActionTooltipHook.Original(addon, numberArrayData, stringArrayData);
    }

    private void OnActionDetailRequestedUpdate(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRequestedUpdateArgs argsFormat) return;

        var context = new ActionDetailTooltipContext
        (
            args.Addon.ToStruct(),
            (NumberArrayData*)argsFormat.NumberArrayData,
            (StringArrayData*)argsFormat.StringArrayData
        );

        InvokeActionDetailEvents(context);
    }

    private void ShowTooltipDetour
    (
        AtkTooltipManager*                manager,
        AtkTooltipType                    type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args,
        void*                             unkDelegate,
        byte                              unk7,
        byte                              unk8
    )
    {
        var context = new TooltipShowContext(manager, type, parentID, targetNode, args);

        InvokeTooltipShowRules(context);
        InvokeTooltipShowEvents(context);

        if (context.TryGetWeather(out _, out _))
            weatherTooltipText = context.Text;

        ShowTooltipHook?.Original(manager, type, parentID, targetNode, args, unkDelegate, unk7, unk8);
    }

    #endregion

    #region 调度

    private void InvokeItemRules(ItemTooltipContext context)
    {
        if (!rulesCollection.TryGetValue(TooltipRuleType.Item, out var rules)) return;

        foreach (var rule in rules)
        {
            try
            {
                ((ItemTooltipRuleDelegate)rule.Method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void InvokeActionRules(ActionTooltipContext context)
    {
        if (!rulesCollection.TryGetValue(TooltipRuleType.Action, out var rules)) return;

        foreach (var rule in rules)
        {
            try
            {
                ((ActionTooltipRuleDelegate)rule.Method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void InvokeTooltipShowRules(TooltipShowContext context)
    {
        if (!rulesCollection.TryGetValue(TooltipRuleType.Show, out var rules)) return;

        foreach (var rule in rules)
        {
            try
            {
                ((TooltipShowRuleDelegate)rule.Method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void InvokeItemEvents(ItemTooltipContext context)
    {
        if (!eventsCollection.TryGetValue(typeof(ItemTooltipEventDelegate), out var methods)) return;

        foreach (var method in methods)
        {
            try
            {
                ((ItemTooltipEventDelegate)method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void InvokeActionEvents(ActionTooltipContext context)
    {
        if (!eventsCollection.TryGetValue(typeof(ActionTooltipEventDelegate), out var methods)) return;

        foreach (var method in methods)
        {
            try
            {
                ((ActionTooltipEventDelegate)method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void InvokeActionDetailEvents(ActionDetailTooltipContext context)
    {
        if (!eventsCollection.TryGetValue(typeof(ActionDetailTooltipEventDelegate), out var methods)) return;

        foreach (var method in methods)
        {
            try
            {
                ((ActionDetailTooltipEventDelegate)method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void InvokeTooltipShowEvents(TooltipShowContext context)
    {
        if (!eventsCollection.TryGetValue(typeof(TooltipShowEventDelegate), out var methods)) return;

        foreach (var method in methods)
        {
            try
            {
                ((TooltipShowEventDelegate)method)(context);
            }
            catch
            {
                // ignored
            }
        }
    }

    #endregion
}

#region 自定义类

public sealed class TooltipRule
{
    internal TooltipRule(TooltipRuleType type, Delegate method)
    {
        Type   = type;
        Method = method;
    }

    internal TooltipRuleType Type { get; }

    internal Delegate Method { get; }
}

internal enum TooltipRuleType
{
    Item,
    Action,
    Show
}

public unsafe class ItemTooltipContext
{
    internal ItemTooltipContext(uint itemID, AtkUnitBase* addon, NumberArrayData* numberArray, StringArrayData* stringArray)
    {
        ItemID      = itemID;
        Addon       = addon;
        NumberArray = numberArray;
        StringArray = stringArray;
    }

    public uint ItemID { get; }

    public AtkUnitBase* Addon { get; }

    public NumberArrayData* NumberArray { get; }

    public StringArrayData* StringArray { get; }

    public ReadOnlySeString Get(TooltipItemType type) =>
        TooltipTextHelper.Get(StringArray, (int)type);

    public void Set(TooltipItemType type, ReadOnlySeString text) =>
        TooltipTextHelper.Set(StringArray, (int)type, text);

    public void Append(TooltipItemType type, ReadOnlySeString text)
    {
        Set(type, TooltipTextHelper.Append(Get(type), text));
    }

    public void Prepend(TooltipItemType type, ReadOnlySeString text)
    {
        Set(type, TooltipTextHelper.Prepend(Get(type), text));
    }

    public void Replace(TooltipItemType type, Func<ReadOnlySeString, ReadOnlySeString> replace) =>
        Set(type, replace(Get(type)));

    public void Replace(TooltipItemType type, string regexPattern, string replacement) =>
        Set(type, TooltipTextHelper.Replace(Get(type), regexPattern, replacement));
}

public unsafe class ActionTooltipContext
{
    internal ActionTooltipContext
    (
        uint                actionID,
        uint                originalActionID,
        TooltipActionDetail actionDetail,
        AtkUnitBase*        addon,
        NumberArrayData*    numberArray,
        StringArrayData*    stringArray
    )
    {
        ActionID          = actionID;
        OriginalActionID  = originalActionID;
        ActionDetail      = actionDetail;
        Addon             = addon;
        NumberArray       = numberArray;
        StringArray       = stringArray;
    }

    public uint ActionID { get; }

    public uint OriginalActionID { get; }

    public TooltipActionDetail ActionDetail { get; }

    public AtkUnitBase* Addon { get; }

    public NumberArrayData* NumberArray { get; }

    public StringArrayData* StringArray { get; }

    public ReadOnlySeString Get(TooltipActionType type) =>
        TooltipTextHelper.Get(StringArray, (int)type);

    public void Set(TooltipActionType type, ReadOnlySeString text) =>
        TooltipTextHelper.Set(StringArray, (int)type, text);

    public void Append(TooltipActionType type, ReadOnlySeString text)
    {
        Set(type, TooltipTextHelper.Append(Get(type), text));
    }

    public void Prepend(TooltipActionType type, ReadOnlySeString text)
    {
        Set(type, TooltipTextHelper.Prepend(Get(type), text));
    }

    public void Replace(TooltipActionType type, Func<ReadOnlySeString, ReadOnlySeString> replace) =>
        Set(type, replace(Get(type)));

    public void Replace(TooltipActionType type, string regexPattern, string replacement) =>
        Set(type, TooltipTextHelper.Replace(Get(type), regexPattern, replacement));
}

public unsafe class ActionDetailTooltipContext
{
    internal ActionDetailTooltipContext(AtkUnitBase* addon, NumberArrayData* numberArray, StringArrayData* stringArray)
    {
        Addon       = addon;
        NumberArray = numberArray;
        StringArray = stringArray;
    }

    public AtkUnitBase* Addon { get; }

    public NumberArrayData* NumberArray { get; }

    public StringArrayData* StringArray { get; }
}

public unsafe class TooltipShowContext
{
    internal TooltipShowContext
    (
        AtkTooltipManager*                manager,
        AtkTooltipType                    type,
        ushort                            parentID,
        AtkResNode*                       targetNode,
        AtkTooltipManager.AtkTooltipArgs* args
    )
    {
        Manager    = manager;
        Type       = type;
        ParentID   = parentID;
        TargetNode = targetNode;
        Args       = args;
    }

    public AtkTooltipManager* Manager { get; }

    public AtkTooltipType Type { get; }

    public ushort ParentID { get; }

    public AtkResNode* TargetNode { get; }

    public AtkTooltipManager.AtkTooltipArgs* Args { get; }

    public ReadOnlySeString Text =>
        Args == null || Args->TextArgs.Text.Value == null ? new() : TooltipTextHelper.Get(Args->TextArgs.Text);

    public void SetText(ReadOnlySeString text) =>
        TooltipTextHelper.Set(ref Args->TextArgs.Text, text);

    public void AppendText(ReadOnlySeString text)
    {
        SetText(TooltipTextHelper.Append(Text, text));
    }

    public void PrependText(ReadOnlySeString text)
    {
        SetText(TooltipTextHelper.Prepend(Text, text));
    }

    public void ReplaceText(Func<ReadOnlySeString, ReadOnlySeString> replace) =>
        SetText(replace(Text));

    public void ReplaceText(string regexPattern, string replacement) =>
        SetText(TooltipTextHelper.Replace(Text, regexPattern, replacement));

    public bool TryGetWeather(out uint weatherID, out Weather weather)
    {
        weatherID = 0;
        weather   = default;

        try
        {
            if (TargetNode == null || NaviMap == null || ParentID != NaviMap->Id)
                return false;

            var compNode = TargetNode->ParentNode->GetAsAtkComponentNode();
            if (compNode == null)
                return false;

            var imageNode = compNode->Component->UldManager.SearchNodeById(3)->GetAsAtkImageNode();
            if (imageNode == null)
                return false;

            var iconID = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
            weatherID = WeatherManager.Instance()->WeatherId;

            return LuminaGetter.TryGetRow(weatherID, out weather) && weather.Icon == iconID;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetStatusID(out uint statusID)
    {
        statusID = 0;

        try
        {
            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer || TargetNode == null)
                return false;

            var imageNode = TargetNode->GetAsAtkImageNode();
            if (imageNode == null)
                return false;

            var iconID = imageNode->PartsList->Parts[imageNode->PartId].UldAsset->AtkTexture.Resource->IconId;
            if (iconID is < 210_000 or > 230_000 || Args->TextArgs.Text.Value == null)
                return false;

            Dictionary<uint, uint> iconStatusIDMap = [];

            if (TargetManager.Target is { } currentTarget && currentTarget.Address != localPlayer.Address)
                AddStatusesToMap(currentTarget.ToBCStruct()->StatusManager, ref iconStatusIDMap);

            if (TargetManager.FocusTarget is { } focusTarget)
                AddStatusesToMap(focusTarget.ToBCStruct()->StatusManager, ref iconStatusIDMap);

            foreach (var member in AgentHUD.Instance()->PartyMembers.ToArray().Where(member => member.Index != 0))
            {
                if (member.Object != null)
                    AddStatusesToMap(member.Object->StatusManager, ref iconStatusIDMap);
            }

            AddStatusesToMap(localPlayer.ToBCStruct()->StatusManager, ref iconStatusIDMap);

            return iconStatusIDMap.TryGetValue(iconID, out statusID) && statusID != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AddStatusesToMap(StatusManager statusesManager, ref Dictionary<uint, uint> map)
    {
        foreach (var statusEntry in statusesManager.Status)
        {
            if (statusEntry.StatusId == 0) continue;
            if (!LuminaGetter.TryGetRow<RowStatus>(statusEntry.StatusId, out var status))
                continue;

            map.TryAdd(status.Icon, status.RowId);

            for (var i = 1; i <= statusEntry.Param; i++)
                map.TryAdd((uint)(status.Icon + i), status.RowId);
        }
    }
}

public class TooltipActionDetail
{
    public DetailKind Category;
    public uint       ID;
    public int        Flag;
    public bool       IsLovmActionDetail;
}

internal static unsafe class TooltipTextHelper
{
    public static ReadOnlySeString Get(StringArrayData* stringArrayData, int index)
    {
        if (stringArrayData == null || index < 0 || index >= stringArrayData->Size)
            return new();

        return Get(stringArrayData->StringArray[index]);
    }

    public static ReadOnlySeString Get(CStringPointer cStringPointer) =>
        cStringPointer.Value == null ? new() : cStringPointer.AsReadOnlySeString();

    public static void Set(StringArrayData* stringArrayData, int index, ReadOnlySeString text)
    {
        if (stringArrayData == null || index < 0 || index >= stringArrayData->Size)
            return;

        stringArrayData->SetValue(index, text.ToDalamudString().EncodeWithNullTerminator(), false);
    }

    public static void Set(ref CStringPointer target, ReadOnlySeString text)
    {
        var bytes = text.ToDalamudString().EncodeWithNullTerminator();
        var ptr   = (byte*)Marshal.AllocHGlobal(bytes.Length);

        for (var i = 0; i < bytes.Length; i++)
            ptr[i] = bytes[i];

        target = ptr;
    }

    public static ReadOnlySeString Replace(ReadOnlySeString text, string regexPattern, string replacement)
    {
        if (string.IsNullOrEmpty(regexPattern))
            return text;

        try
        {
            return new ReadOnlySeString(Regex.Replace(text.ExtractText(), regexPattern, replacement));
        }
        catch
        {
            return text;
        }
    }

    public static ReadOnlySeString Append(ReadOnlySeString current, ReadOnlySeString text)
    {
        if (text.IsEmpty)
            return current;

        var currentText = current.ExtractText();
        var appendText  = text.ExtractText();

        if (currentText.EndsWith(appendText, StringComparison.Ordinal))
            return current;

        using var builder = new RentedSeStringBuilder();
        return builder.Builder.Append(current).Append(text).ToReadOnlySeString();
    }

    public static ReadOnlySeString Prepend(ReadOnlySeString current, ReadOnlySeString text)
    {
        if (text.IsEmpty)
            return current;

        var currentText = current.ExtractText();
        var prependText = text.ExtractText();

        if (currentText.StartsWith(prependText, StringComparison.Ordinal))
            return current;

        using var builder = new RentedSeStringBuilder();
        return builder.Builder.Append(text).Append(current).ToReadOnlySeString();
    }
}

public enum TooltipItemType : byte
{
    ItemName,
    GlamourName,
    ItemUICategory,
    ItemDescription                       = 13,
    Effects                               = 16,
    ClassJobCategory                      = 22,
    DurabilityPercent                     = 28,
    SpiritbondPercent                     = 30,
    ExtractableProjectableDesynthesizable = 35,
    Param0                                = 37,
    Param1                                = 38,
    Param2                                = 39,
    Param3                                = 40,
    Param4                                = 41,
    Param5                                = 42,
    ControlsDisplay                       = 64
}

public enum TooltipActionType
{
    ActionName,
    ActionKind,
    Unknown02, // 与ActionKind共享同一位置
    RangeText,
    RangeValue,
    RadiusText,
    RadiusValue,
    MPCostText,
    MPCostValue,
    RecastText,
    RecastValue,
    CastText,
    CastValue,
    Description,
    Level,
    ClassJobAbbr
}

#endregion
