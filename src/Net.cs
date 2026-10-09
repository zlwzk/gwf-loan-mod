using System;
using System.Collections.Generic;
using Mirror;

namespace GwfLoanMod
{
    /// <summary>借款请求（客户端 → Host）。</summary>
    public struct LoanRequestMsg : NetworkMessage
    {
        public ulong borrower;   // 请求者 steamId
        public ulong lender;     // 想向谁借
        public double amount;
    }

    /// <summary>债主的裁决（债主客户端 → Host）。</summary>
    public struct LoanDecisionMsg : NetworkMessage
    {
        public int requestId;
        public ulong lender;
        public bool accept;
    }

    /// <summary>Host → 债主：弹出同意/拒绝面板。</summary>
    public struct LoanPromptMsg : NetworkMessage
    {
        public int requestId;
        public ulong borrower;
        public string borrowerName;
        public double amount;
        public double expiresAt; // Time.time 语义下本地不可比；这里用剩余秒数
        public double remainSec;
    }

    /// <summary>Host → 所有客户端：全量账本快照。</summary>
    public struct LedgerSnapshotMsg : NetworkMessage
    {
        public string json;
    }

    /// <summary>Host → 借款人：弹幕（劝诫/通知）。</summary>
    public struct DanmakuMsg : NetworkMessage
    {
        public string text;
    }

    /// <summary>客户端 → Host：请求变更房主设置（Host 校验来源，仅房主生效）。</summary>
    public struct SettingsChangeMsg : NetworkMessage
    {
        public bool setEnabled;
        public bool newValue;
        public int repayOrder;      // RepayOrder
        public double debtCap;
        public bool danmakuEnabled;
        public bool hardBetCap;
        /// <summary>请求重新平均分配（仅房主生效）。</summary>
        public bool redistribute;
        /// <summary>sendersteamId，仅用于日志；真实身份以连接为准。</summary>
        public ulong senderId;
    }

    /// <summary>Host → 所有客户端：toast 提示（左下角小通知）。</summary>
    public struct ToastMsg : NetworkMessage
    {
        public string text;
    }

    /// <summary>客户端 → Host：团队贷款操作（Host 校验来源，仅房主生效）。</summary>
    public struct TeamLoanMsg : NetworkMessage
    {
        public int action;      // 0 = 申请贷款，1 = 免除未还部分
        public double amount;  // 申请金额
        public double rate;    // 赢利还款比例（0.05 ~ 1.0）
    }

    /// <summary>Mirror 消息注册与收发封装（Host / 客户端通用）。</summary>
    public static class LoanNet
    {
        public const short LoanRequestId = 4801;
        public const short LoanDecisionId = 4802;
        public const short LoanPromptId = 4803;
        public const short LedgerSnapshotId = 4804;
        public const short DanmakuId = 4805;
        public const short SettingsChangeId = 4806;
        public const short ToastId = 4807;
        public const short TeamLoanId = 4808;

        private static bool _serializersReady;

        /// <summary>序列化往返自检结果（Awake 时由插件用 BepInEx logger 打印；Debug.Log 不进 BepInEx 日志文件）。</summary>
        public static string SerializerSelfTestResult = "(未运行)";

        /// <summary>
        /// mod 程序集没有经过 Mirror weaver（weaver 只在游戏本体编译期注入），
        /// 因此 Writer&lt;T&gt;.write / Reader&lt;T&gt;.read 全是 null ——
        /// Mirror 发送时只打一行 "No writer found for ..." 就把消息静默丢弃，
        /// 表现为「按钮点了没反应、功能开启不了」（单人 Host 与多人客户端都一样）。
        /// 这里在运行时手工注册序列化（weaver 做的正是这件事）。
        /// </summary>
        public static void EnsureSerializers()
        {
            if (_serializersReady) return;
            _serializersReady = true;

            Writer<LoanRequestMsg>.write = (w, m) =>
            {
                w.WriteULong(m.borrower);
                w.WriteULong(m.lender);
                w.WriteDouble(m.amount);
            };
            Reader<LoanRequestMsg>.read = r => new LoanRequestMsg
            {
                borrower = r.ReadULong(),
                lender = r.ReadULong(),
                amount = r.ReadDouble(),
            };

            Writer<LoanDecisionMsg>.write = (w, m) =>
            {
                w.WriteInt(m.requestId);
                w.WriteULong(m.lender);
                w.WriteBool(m.accept);
            };
            Reader<LoanDecisionMsg>.read = r => new LoanDecisionMsg
            {
                requestId = r.ReadInt(),
                lender = r.ReadULong(),
                accept = r.ReadBool(),
            };

            Writer<LoanPromptMsg>.write = (w, m) =>
            {
                w.WriteInt(m.requestId);
                w.WriteULong(m.borrower);
                w.WriteString(m.borrowerName);
                w.WriteDouble(m.amount);
                w.WriteDouble(m.expiresAt);
                w.WriteDouble(m.remainSec);
            };
            Reader<LoanPromptMsg>.read = r => new LoanPromptMsg
            {
                requestId = r.ReadInt(),
                borrower = r.ReadULong(),
                borrowerName = r.ReadString(),
                amount = r.ReadDouble(),
                expiresAt = r.ReadDouble(),
                remainSec = r.ReadDouble(),
            };

            Writer<LedgerSnapshotMsg>.write = (w, m) => w.WriteString(m.json);
            Reader<LedgerSnapshotMsg>.read = r => new LedgerSnapshotMsg { json = r.ReadString() };

            Writer<DanmakuMsg>.write = (w, m) => w.WriteString(m.text);
            Reader<DanmakuMsg>.read = r => new DanmakuMsg { text = r.ReadString() };

            Writer<SettingsChangeMsg>.write = (w, m) =>
            {
                w.WriteBool(m.setEnabled);
                w.WriteBool(m.newValue);
                w.WriteInt(m.repayOrder);
                w.WriteDouble(m.debtCap);
                w.WriteBool(m.danmakuEnabled);
                w.WriteBool(m.hardBetCap);
                w.WriteBool(m.redistribute);
                w.WriteULong(m.senderId);
            };
            Reader<SettingsChangeMsg>.read = r => new SettingsChangeMsg
            {
                setEnabled = r.ReadBool(),
                newValue = r.ReadBool(),
                repayOrder = r.ReadInt(),
                debtCap = r.ReadDouble(),
                danmakuEnabled = r.ReadBool(),
                hardBetCap = r.ReadBool(),
                redistribute = r.ReadBool(),
                senderId = r.ReadULong(),
            };

            Writer<ToastMsg>.write = (w, m) => w.WriteString(m.text);
            Reader<ToastMsg>.read = r => new ToastMsg { text = r.ReadString() };

            Writer<TeamLoanMsg>.write = (w, m) =>
            {
                w.WriteInt(m.action);
                w.WriteDouble(m.amount);
                w.WriteDouble(m.rate);
            };
            Reader<TeamLoanMsg>.read = r => new TeamLoanMsg
            {
                action = r.ReadInt(),
                amount = r.ReadDouble(),
                rate = r.ReadDouble(),
            };

            SerializerSelfTestResult = "8 类消息序列化器已手工注册（weaver 替代）；";

            // 内存往返自检：序列化 → 反序列化，直接验证消息链路（不用进房间就能在日志里确认）
            try
            {
                var probe = new SettingsChangeMsg
                {
                    setEnabled = true, newValue = true, repayOrder = 2,
                    debtCap = 123.5, danmakuEnabled = true, hardBetCap = false,
                    redistribute = true, senderId = 987654321UL,
                };
                using (var w = NetworkWriterPool.Get())
                {
                    NetworkMessages.Pack(probe, w);
                    using (var r = NetworkReaderPool.Get(w.ToArraySegment()))
                    {
                        ushort id;
                        NetworkMessages.UnpackId(r, out id);
                        var back = r.Read<SettingsChangeMsg>();
                        bool ok = id == NetworkMessageId<SettingsChangeMsg>.Id
                                  && back.setEnabled && back.newValue && back.repayOrder == 2
                                  && Math.Abs(back.debtCap - 123.5) < 1e-9
                                  && back.danmakuEnabled && !back.hardBetCap
                                  && back.redistribute && back.senderId == 987654321UL;
                        UnityEngine.Debug.Log(ok
                            ? "[LoanMod] 消息序列化往返自检通过（SettingsChangeMsg, id=" + id + "）"
                            : "[LoanMod] 消息序列化往返自检失败！字段不一致");
                        SerializerSelfTestResult += ok
                            ? "往返自检通过（SettingsChangeMsg, id=" + id + "）"
                            : "往返自检失败！字段不一致";
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[LoanMod] 消息序列化自检异常: " + e);
                SerializerSelfTestResult += "自检异常: " + e.Message;
            }
        }

        /// <summary>消息类型名：注册时用的 key（Mirror 按 ushort 消息 ID 分发）。</summary>
        public static void RegisterAll(
            Action<NetworkConnectionToClient, LoanRequestMsg> onRequest,
            Action<NetworkConnectionToClient, LoanDecisionMsg> onDecision,
            Action<NetworkConnectionToClient, SettingsChangeMsg> onSettings,
            Action<LoanPromptMsg> onPrompt,
            Action<LedgerSnapshotMsg> onSnapshot,
            Action<DanmakuMsg> onDanmaku,
            Action<ToastMsg> onToast,
            Action<NetworkConnectionToClient, TeamLoanMsg> onTeamLoan)
        {
            EnsureSerializers();
            if (NetworkServer.active)
            {
                NetworkServer.RegisterHandler<LoanRequestMsg>(onRequest, false);
                NetworkServer.RegisterHandler<LoanDecisionMsg>(onDecision, false);
                NetworkServer.RegisterHandler<SettingsChangeMsg>(onSettings, false);
                NetworkServer.RegisterHandler<TeamLoanMsg>(onTeamLoan, false);
            }
            if (NetworkClient.active)
            {
                NetworkClient.RegisterHandler<LoanPromptMsg>(onPrompt, false);
                NetworkClient.RegisterHandler<LedgerSnapshotMsg>(onSnapshot, false);
                NetworkClient.RegisterHandler<DanmakuMsg>(onDanmaku, false);
                NetworkClient.RegisterHandler<ToastMsg>(onToast, false);
            }
        }

        public static void SendToServer<T>(T msg) where T : struct, NetworkMessage
        {
            if (NetworkClient.active && NetworkClient.connection != null)
                NetworkClient.connection.Send(msg);
        }

        public static void SendToConnection<T>(int connId, T msg) where T : struct, NetworkMessage
        {
            NetworkConnectionToClient conn;
            if (NetworkServer.connections.TryGetValue(connId, out conn) && conn != null)
                conn.Send(msg);
        }

        public static void BroadcastLedger(LedgerSnapshot snap)
        {
            var msg = new LedgerSnapshotMsg { json = snap.ToJson() };
            NetworkServer.SendToAll(msg);
            // Host 自己也吃一份（Host 的 NetworkClient 也会收到 SendToAll）
        }

        public static void BroadcastToast(string text)
        {
            NetworkServer.SendToAll(new ToastMsg { text = text });
        }
    }
}
