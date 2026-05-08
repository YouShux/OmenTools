using System.Collections.Concurrent;
using System.Collections.Immutable;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.OmenService.Abstractions;

namespace OmenTools.OmenService;

public unsafe class TooltipManager : OmenServiceBase<TooltipManager>
{
    #region 公开订阅

    public delegate void ItemTooltipUpdateDelegate(uint itemID, ref List<TooltipItemModification> modifications);
    
    public delegate void ActionTooltipUpdateDelegate(DetailKind actionKind, uint actionID, ref List<TooltipActionModification> modifications);

    #endregion

    #region 公开方法

    /// <summary>
    ///     触发一次物品工具信息界面更新
    /// </summary>
    /// <remarks>
    ///     在你需要更新内容时调用
    /// </remarks>
    public void TriggerItemDetailUpdate()
    {
        DService.Instance().Framework.RunOnFrameworkThread
        (() =>
            {
                if (!ItemDetail->IsAddonAndNodesReady()) return;
                ItemDetail->OnRequestedUpdate
                (
                    AtkStage.Instance()->GetNumberArrayData(),
                    AtkStage.Instance()->GetStringArrayData()
                );
                
                DLog.Verbose($"{nameof(TooltipManager)}: 触发更新物品工具信息界面");
            }
        );
    }
    
    /// <summary>
    ///     触发一次技能工具信息界面更新
    /// </summary>
    /// <remarks>
    ///     在你需要更新内容时调用
    /// </remarks>
    public void TriggerActionDetailUpdate()
    {
        DService.Instance().Framework.RunOnFrameworkThread
        (() =>
            {
                if (!ActionDetail->IsAddonAndNodesReady()) return;
                ActionDetail->OnRequestedUpdate
                (
                    AtkStage.Instance()->GetNumberArrayData(),
                    AtkStage.Instance()->GetStringArrayData()
                );
                
                DLog.Verbose($"{nameof(TooltipManager)}: 触发更新技能工具信息界面");
            }
        );
    }

    /// <summary>
    ///     获取原始物品工具信息文本
    /// </summary>
    /// <remarks>
    ///     请确保在 <see cref="ItemTooltipUpdateDelegate" /> 期间调用
    /// </remarks>
    public ReadOnlySeString GetOriginalItemTooltipText(TooltipItemType target) =>
        itemOriginalTexts[(int)target];

    /// <summary>
    ///     获取原始技能工具信息文本
    /// </summary>
    /// <remarks>
    ///     请确保在 <see cref="ActionTooltipUpdateDelegate" /> 期间调用
    /// </remarks>
    public ReadOnlySeString GetOriginalActionTooltipText(TooltipActionType target) =>
        actionOriginalTexts[(int)target];

    #region 订阅

    public void RegItem(ItemTooltipUpdateDelegate method, params ItemTooltipUpdateDelegate[] methods) =>
        RegisterGeneric(method, methods);

    public void RegAction(ActionTooltipUpdateDelegate method, params ActionTooltipUpdateDelegate[] methods) =>
        RegisterGeneric(method, methods);

    public void Unreg(params ItemTooltipUpdateDelegate[] methods) =>
        UnregisterGeneric(methods);

    public void Unreg(params ActionTooltipUpdateDelegate[] methods) =>
        UnregisterGeneric(methods);

    #endregion

    #endregion
    
    #region 私有状态

    // 上个物品
    private uint lastItemID;
    
    // 物品原始文本
    private ReadOnlySeString[] itemOriginalTexts = new ReadOnlySeString[65];
    
    // 上个技能
    private (DetailKind Kind, uint ID) lastActionInfo;
    
    // 技能原始文本
    private ReadOnlySeString[] actionOriginalTexts = new ReadOnlySeString[16];
    
    private readonly ConcurrentDictionary<Type, ImmutableList<Delegate>> methodsCollection = [];

    #endregion
    
    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", OnItemDetailUpdate);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ActionDetail", OnActionDetailUpdate);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnItemDetailUpdate);
        DService.Instance().AddonLifecycle.UnregisterListener(OnActionDetailUpdate);
    }

    // 物品
    private void OnItemDetailUpdate(AddonEvent type, AddonArgs args)
    {
        var stringArrayData = AtkStage.Instance()->GetStringArrayData(StringArrayType.ItemDetail);
        var textArray       = stringArrayData->StringArray;

        var currentItemID = GetBaseItemID(AgentItemDetail.Instance()->ItemId);
        if (currentItemID != lastItemID)
        {
            lastItemID = currentItemID;
            DLog.Verbose($"[{nameof(TooltipManager)}] 物品工具提示内容刷新: {lastItemID}");

            for (var i = 0; i < itemOriginalTexts.Length; i++)
            {
                if (!textArray[i].HasValue)
                {
                    itemOriginalTexts[i] = new ReadOnlySeString();
                    continue;
                }
                
                itemOriginalTexts[i] = new ReadOnlySeString(new CStringPointer(textArray[i].Value).AsSpan());
            }
        }
        
        DLog.Verbose($"[{nameof(TooltipManager)}] 物品工具提示刷新: {lastItemID}");

        // 这里是文本修改
        if (!methodsCollection.TryGetValue(typeof(ItemTooltipUpdateDelegate), out var itemDelegates))
            return;

        // 收集
        var modificationsByTarget = new Dictionary
        <
            TooltipItemType,
            (
                List<TooltipItemModification> Prepend,
                List<TooltipItemModification> Body,
                List<TooltipItemModification> Append
            )
        >();
        
        foreach (var itemDelegate in itemDelegates)
        {
            var tooltipUpdate = (ItemTooltipUpdateDelegate)itemDelegate;

            List<TooltipItemModification> modifications = [];
            tooltipUpdate(currentItemID, ref modifications);

            foreach (var modification in modifications)
            {
                if (!modificationsByTarget.TryGetValue(modification.Target, out var targetModifications))
                {
                    targetModifications =
                        (
                            [],
                            [],
                            []
                        );
                    modificationsByTarget[modification.Target] = targetModifications;
                }

                switch (modification.Type)
                {
                    case TooltipModificationType.Prepend:
                        targetModifications.Prepend.Add(modification);
                        break;
                    case TooltipModificationType.Contribute:
                        targetModifications.Body.Add(modification);
                        break;
                    case TooltipModificationType.Append:
                        targetModifications.Append.Add(modification);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(modification.Type));
                }
            }
        }
        
        // 形成
        foreach (var (target, targetModifications) in modificationsByTarget)
        {
            var index = (int)target;
            if ((uint)index >= (uint)itemOriginalTexts.Length) continue;

            using var rentedBuilder = new RentedSeStringBuilder();
            var builder = rentedBuilder.Builder;
            
            var hasText = false;
            
            foreach (var modification in targetModifications.Prepend)
            {
                if (modification.Text.IsEmpty)
                    continue;
                
                if (hasText) 
                    builder.AppendNewLine();

                builder.Append(modification.Text);
                hasText = true;
            }

            if (targetModifications.Body.Count <= 0)
            {
                if (!itemOriginalTexts[index].IsEmpty)
                {
                    if (hasText)
                        builder.AppendNewLine();

                    builder.Append(itemOriginalTexts[index]);
                    hasText = true;
                }
            }
            else
            {
                foreach (var modification in targetModifications.Body)
                {
                    if (modification.Text.IsEmpty)
                        continue;
                    
                    if (hasText)
                        builder.AppendNewLine();

                    builder.Append(modification.Text);
                    hasText = true;
                }
            }

            foreach (var modification in targetModifications.Append)
            {
                if (modification.Text.IsEmpty)
                    continue;
                
                if (hasText) 
                    builder.AppendNewLine();

                builder.Append(modification.Text);
                hasText = true;
            }

            if (hasText)
                stringArrayData->SetValue(index, builder.GetViewAsSpan());
        }
    }
    
    // 技能
    private void OnActionDetailUpdate(AddonEvent type, AddonArgs args)
    {
        var stringArrayData = AtkStage.Instance()->GetStringArrayData(StringArrayType.ActionDetail);
        var textArray       = stringArrayData->StringArray;

        var currentActionInfo = (AgentActionDetail.Instance()->ActionKind, AgentActionDetail.Instance()->ActionId);
        if (currentActionInfo != lastActionInfo)
        {
            lastActionInfo = currentActionInfo;
            DLog.Verbose($"[{nameof(TooltipManager)}] 技能工具提示内容刷新: {lastActionInfo}");

            for (var i = 0; i < actionOriginalTexts.Length; i++)
            {
                if (!textArray[i].HasValue)
                {
                    actionOriginalTexts[i] = new ReadOnlySeString();
                    continue;
                }
                
                actionOriginalTexts[i] = new ReadOnlySeString(new CStringPointer(textArray[i].Value).AsSpan());
            }
        }
        
        DLog.Verbose($"[{nameof(TooltipManager)}] 物品工具提示刷新: {lastItemID}");
        
        // 这里是文本修改
        if (!methodsCollection.TryGetValue(typeof(ActionTooltipUpdateDelegate), out var actionDelegates))
            return;

        var modificationsByTarget = new Dictionary
        <
            TooltipActionType,
            (
                List<TooltipActionModification> Prepend,
                List<TooltipActionModification> Body,
                List<TooltipActionModification> Append
            )
        >();

        foreach (var actionDelegate in actionDelegates)
        {
            var tooltipUpdate = (ActionTooltipUpdateDelegate)actionDelegate;

            List<TooltipActionModification> modifications = [];
            tooltipUpdate(currentActionInfo.Item1, currentActionInfo.Item2, ref modifications);

            foreach (var modification in modifications)
            {
                if (!modificationsByTarget.TryGetValue(modification.Target, out var targetModifications))
                {
                    targetModifications =
                    (
                        [],
                        [],
                        []
                    );
                    modificationsByTarget[modification.Target] = targetModifications;
                }

                switch (modification.Type)
                {
                    case TooltipModificationType.Prepend:
                        targetModifications.Prepend.Add(modification);
                        break;
                    case TooltipModificationType.Contribute:
                        targetModifications.Body.Add(modification);
                        break;
                    case TooltipModificationType.Append:
                        targetModifications.Append.Add(modification);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(modification.Type));
                }
            }
        }

        foreach (var (target, targetModifications) in modificationsByTarget)
        {
            var index = (int)target;
            if ((uint)index >= (uint)actionOriginalTexts.Length) continue;

            using var rentedBuilder = new RentedSeStringBuilder();

            var builder = rentedBuilder.Builder;
            var hasText = false;

            foreach (var modification in targetModifications.Prepend)
            {
                if (modification.Text.IsEmpty)
                    continue;
                
                if (hasText)
                    builder.AppendNewLine();

                builder.Append(modification.Text);
                hasText = true;
            }

            if (targetModifications.Body.Count > 0)
            {
                foreach (var modification in targetModifications.Body)
                {
                    if (modification.Text.IsEmpty)
                        continue;
                    
                    if (hasText)
                        builder.AppendNewLine();

                    builder.Append(modification.Text);
                    hasText = true;
                }
            }
            else if (!actionOriginalTexts[index].IsEmpty)
            {
                if (hasText)
                    builder.AppendNewLine();

                builder.Append(actionOriginalTexts[index]);
                hasText = true;
            }

            foreach (var modification in targetModifications.Append)
            {
                if (modification.Text.IsEmpty)
                    continue;
                
                if (hasText)
                    builder.AppendNewLine();

                builder.Append(modification.Text);
                hasText = true;
            }

            if (hasText)
                stringArrayData->SetValue(index, builder.GetViewAsSpan());
        }
    }
    
    // 注册
    private bool RegisterGeneric<T>(T method, params T[] methods) where T : Delegate
    {
        var type = typeof(T);

        methodsCollection.AddOrUpdate
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

        return true;
    }

    // 取消注册
    private bool UnregisterGeneric<T>(params T[] methods) where T : Delegate
    {
        if (methods is not { Length: > 0 }) return false;

        var type = typeof(T);

        while (methodsCollection.TryGetValue(type, out var currentList))
        {
            var newList = currentList.RemoveRange(methods);

            if (newList == currentList)
                return false;

            if (newList.IsEmpty)
            {
                var kvp = new KeyValuePair<Type, ImmutableList<Delegate>>(type, currentList);
                if (((ICollection<KeyValuePair<Type, ImmutableList<Delegate>>>)methodsCollection).Remove(kvp))
                    return true;
            }
            else
            {
                if (methodsCollection.TryUpdate(type, newList, currentList))
                    return true;
            }
        }

        return false;
    }

    #region 工具

    private static uint GetBaseItemID(uint itemID)
    {
        switch (itemID)
        {
            // HQ
            case > 100_0000:
                itemID %= 100_0000;
                break;
            
            // 收藏品
            case > 50_0000:
                itemID %= 50_0000;
                break;
        }

        return itemID;
    }

    #endregion
}
