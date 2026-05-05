using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class NoviceNetworkCommand : ExecuteCommandBase
{
    /// <summary>
    ///     解除新人状态
    /// </summary>
    public static void DismissNovice() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.DissmissNovice);

    /// <summary>
    ///     成为新人状态
    /// </summary>
    public static void EnableNovice() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.EnableNovice);

    /// <summary>
    ///     指导者启用或解除自动加入新人频道设置
    /// </summary>
    public static void ToggleMentorAutoJoin() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.MentorAutoJoinNoviceNetwork);

    /// <summary>
    ///     接受新人频道邀请
    /// </summary>
    public static void AcceptInvitation() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.AcceptNoviceNetworkInvitation);

    /// <summary>
    ///     拒绝新人频道邀请
    /// </summary>
    public static void RejectInvitation() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.AcceptNoviceNetworkInvitation, 1);

    /// <summary>
    ///     解除回归者状态
    /// </summary>
    public static void DismissReturner() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.DismissReturner);

    /// <summary>
    ///     刷新新人频道状态
    /// </summary>
    public static void Request() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestNoviceNetwork);

    /// <summary>
    ///     认领回归者时是否一并加入新人频道
    /// </summary>
    public static void JoinAsReturner() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.ReturnerJoinNoviceNetwork);
}
