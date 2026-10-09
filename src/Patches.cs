using System;
using System.Collections;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace GwfLoanMod
{
    /// <summary>
    /// Harmony patch：只 patch 服务器端方法，Host（房主）权威。
    /// 全员装同样的 mod → 同样的 patch → 同样的结果，不会产生不同步。
    /// </summary>
    public static class Patches
    {
        /// <summary>由 Plugin 注入，避免 patch 与插件生命周期耦合。</summary>
        public static LoanCore Core;

        // ------------------- 单注上限拦截 -------------------

        /// <summary>
        /// MoneyManager.TryChangeBalance(BigNumber amount, PlayerProfile changer, ChangeType type) [Server]
        /// 下注（amount &lt; 0 且 type == Bet）时：超过个人账户余额 → 拒绝。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.TryChangeBalance))]
        public static bool MoneyManager_TryChangeBalance_Prefix(
            ref bool __result,
            BigNumber amount,
            PlayerProfile changer,
            ChangeType changeType)
        {
            if (Core == null || !Core.Snap.enabled || !Core.Snap.settings.hardBetCap)
                return true; // 不干预
            if (!NetworkServer.active)
                return true; // 只在 Host 上拦截
            if (changeType != ChangeType.Bet)
                return true;
            if (!amount.IsNegative || amount.IsZero)
                return true; // 只拦"扣钱"方向的下注

            double want = amount.Abs.ToDouble();
            ulong sid = changer != null ? changer.steamId : 0ul;
            if (!Core.AllowBet(sid, want))
            {
                __result = false; // 拒绝这笔下注（原方法不执行）
                var acc = Core.Snap.GetAccount(sid);
                Debug.Log($"[Loan] 拦截下注：{changer?.playerName} 想下 {want:F0}，账户余额 {acc?.balance:F0}");
                return false;
            }
            return true;
        }

        // ------------------- 团队贷款：车辆倒计时拦截 -------------------

    /// <summary>
    /// AllPlayersTriggerZone.CountdownRoutine() [Server]
    /// 车辆（回合结束的逃生车）全员上车后的发车倒计时。
    /// 团队贷款到期未还清时：倒计时不会开始（返回一个"空"倒计时避免空引用），
    /// 直到团队还清贷款、达标当前对局金额，或当局回合计时耗尽（届时游戏照常推进，绝不软锁）。
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AllPlayersTriggerZone), "CountdownRoutine")]
    public static bool AllPlayersTriggerZone_CountdownRoutine_Prefix(ref IEnumerator __result)
    {
        if (Core == null || !NetworkServer.active)
            return true;
        try
        {
            if (Core.TeamLoanBlocksVehicle())
            {
                Core.NotifyVehicleBlocked();
                // 用空迭代器替代原倒计时：StartCoroutine 拿到合法枚举，倒计时静默不开始
                __result = ((IEnumerable)new object[0]).GetEnumerator();
                return false;
            }
        }
        catch (Exception e)
        {
            Debug.Log("[Loan] CountdownRoutine prefix err: " + e.Message);
        }
        return true;
    }

    // ------------------- 结算 → 账户 + 自动还款 + 广播 -------------------

        /// <summary>
        /// GameResultsManager.RegisterResult(bet, payout, profile, gameType, ...) [Server]
        /// Postfix：更新借款人份额账户，然后检查自动还款，广播快照。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameResultsManager), nameof(GameResultsManager.RegisterResult))]
        public static void GameResultsManager_RegisterResult_Postfix(
            BigNumber bet,
            BigNumber payout,
            PlayerProfile playerProfile)
        {
            if (Core == null || !Core.Snap.enabled || !NetworkServer.active)
                return;
            try
            {
                if (playerProfile == null)
                    return;
                Core.OnGameResult(playerProfile.steamId, bet.ToDouble(), payout.ToDouble());
                Core.PushRecent($"[结算] {playerProfile.playerName}: bet {bet.ToDouble():F0} / payout {payout.ToDouble():F0}");
                LoanNet.BroadcastLedger(Core.Snap);
            }
            catch (Exception e)
            {
                Debug.Log("[Loan] RegisterResult postfix err: " + e);
            }
        }
    }
}
