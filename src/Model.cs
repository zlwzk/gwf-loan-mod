using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GwfLoanMod
{
    /// <summary>还款顺序（房主可设）。</summary>
    public enum RepayOrder
    {
        /// <summary>债务金额从小到大还。</summary>
        SmallFirst = 0,
        /// <summary>债务金额从大到小还。</summary>
        LargeFirst = 1,
        /// <summary>按借款发生顺序还。</summary>
        Chronological = 2,
    }

    /// <summary>单个玩家的份额账户（由 Host 权威维护，随快照广播）。</summary>
    [Serializable]
    public class AccountState
    {
        public ulong steamId;
        public string name;
        /// <summary>当前可用额度（可为负=欠款未还前透支）。</summary>
        public double balance;
        /// <summary>未偿债务总额（冗余字段，随 loans 计算）。</summary>
        public double debtTotal;
    }

    /// <summary>一张借据。</summary>
    [Serializable]
    public class LoanRecord
    {
        public int id;
        public ulong borrower;   // 借款人 steamId
        public ulong lender;     // 债主 steamId
        public double principal;  // 借款本金
        public double remaining; // 未还金额
        /// <summary>全局递增序号（借款顺序）。</summary>
        public int seq;
        public double takenAt;
        /// <summary>还款事件（用于结算详情展示，Host 端追加）。</summary>
        public List<RepayEvent> repays = new List<RepayEvent>();
    }

    [Serializable]
    public class RepayEvent
    {
        public double amount;
        public double ts;
    }

    /// <summary>团队贷款（向贷款公司借，全队记账）。</summary>
    [Serializable]
    public class TeamLoanState
    {
        /// <summary>有一笔未结清的团队贷款。</summary>
        public bool active;
        public double principal;      // 本金
        public double remaining;     // 未还
        /// <summary>还款比例：每局赢利 × 该比例自动扣还（多赚多还，少赚少还）。</summary>
        public double repayRate = 0.5;
        /// <summary>借款发生在对局内（true：宽限到下下回合结束；false：下一回合结束）。</summary>
        public bool takenDuringDay;
        /// <summary>借款后已经历的回合结束次数（Host 端计数）。</summary>
        public int dayEndsSinceLoan;
        /// <summary>已逾期：新一轮车辆倒计时暂停，直至还清。</summary>
        public bool overdue;
        /// <summary>累计已还。</summary>
        public double totalRepaid;
    }

    /// <summary>房主设置（随快照广播，所有人可见）。</summary>
    [Serializable]
    public class ModSettings
    {
        public bool enabled = false;
        public RepayOrder repayOrder = RepayOrder.Chronological;
        /// <summary>单人未偿债务上限（0 = 不限）。</summary>
        public double debtCap = 0.0;
        /// <summary>借款请求倒计时（秒）。</summary>
        public double requestTimeoutSec = 20.0;
        /// <summary>弹幕开关。</summary>
        public bool danmakuEnabled = true;
        /// <summary>单人单注硬拦截（下注不能超过账户余额）。</summary>
        public bool hardBetCap = true;
        /// <summary>结算后自动还款触发倍数（余额 ≥ 2×未偿总额时触发）。</summary>
        public double autoRepayMultiplier = 2.0;
    }



    /// <summary>全量状态快照：Host → 所有客户端，所有界面读这一份。</summary>
    [Serializable]
    public class LedgerSnapshot
    {
        public ModSettings settings = new ModSettings();

        /// <summary>mod 总开关（转发到 settings.enabled，避免到处写 .settings.enabled）。</summary>
        [JsonIgnore]
        public bool enabled
        {
            get { return settings != null && settings.enabled; }
            set { if (settings != null) settings.enabled = value; }
        }

        public List<AccountState> accounts = new List<AccountState>();
        public List<LoanRecord> loans = new List<LoanRecord>();
        /// <summary>团队贷款（贷款公司）。</summary>
        public TeamLoanState teamLoan = new TeamLoanState();
        /// <summary>每人初始份额（= 开启时共享池 / 人数），同时是单注上限基准。</summary>
        public double perPlayerShare = 0.0;
        /// <summary>开启时共享池总额。</summary>
        public double poolAtEnable = 0.0;
        public int nextLoanId = 1;
        public int nextSeq = 1;
        /// <summary>结算面板展示：最近一次结算的借款动态（Host 填）。</summary>
        public List<string> recentEvents = new List<string>();

        public AccountState GetAccount(ulong steamId)
        {
            return accounts.Find(a => a.steamId == steamId);
        }

        public double DebtOf(ulong steamId)
        {
            double d = 0;
            foreach (var l in loans)
                if (l.borrower == steamId) d += l.remaining;
            return d;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }

        public static LedgerSnapshot FromJson(string json)
        {
            try
            {
                return string.IsNullOrEmpty(json)
                    ? new LedgerSnapshot()
                    : JsonConvert.DeserializeObject<LedgerSnapshot>(json) ?? new LedgerSnapshot();
            }
            catch
            {
                return new LedgerSnapshot();
            }
        }
    }
}
