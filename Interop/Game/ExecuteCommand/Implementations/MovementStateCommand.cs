using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class MovementStateCommand : ExecuteCommandBase
{
    /// <summary>
    ///     进入游泳状态
    /// </summary>
    public static void EnterSwim() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.EnterSwim);

    /// <summary>
    ///     退出游泳状态
    /// </summary>
    public static void LeaveSwim() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LeaveSwim);

    /// <summary>
    ///     进入飞行状态
    /// </summary>
    public static void EnterFlight() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.EnterFlight);
}
