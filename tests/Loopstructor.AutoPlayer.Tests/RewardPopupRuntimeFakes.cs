namespace MetroTD.RewardSystem
{
    internal enum FakeRewardItemType
    {
        SuperModule,
        Disposable
    }

    internal sealed class FakeRewardQueueItem
    {
        public FakeRewardItemType itemType;
        public int remainingSelectionCount;
        public bool isMandatory;
    }

    internal sealed class RewardUIPanel
    {
        public static RewardUIPanel Instance { get; } = new();

        private FakeRewardQueueItem? m_currentQueueItem;
        private readonly List<FakeRewardQueueItem> m_currentRewardQueneItems = new();
        private bool _finished;

        public bool IsActive { get; private set; }
        public string CurrentItemType => m_currentQueueItem?.itemType.ToString() ?? string.Empty;

        public static void Reset(bool mandatory, bool includeNext)
        {
            RewardUIPanel panel = Instance;
            panel.IsActive = true;
            panel._finished = false;
            panel.m_currentQueueItem = new FakeRewardQueueItem
            {
                itemType = FakeRewardItemType.SuperModule,
                remainingSelectionCount = 2,
                isMandatory = mandatory
            };
            panel.m_currentRewardQueneItems.Clear();
            if (includeNext)
            {
                panel.m_currentRewardQueneItems.Add(new FakeRewardQueueItem
                {
                    itemType = FakeRewardItemType.Disposable,
                    remainingSelectionCount = 1,
                    isMandatory = false
                });
            }
        }

        public void SkipHandle()
        {
            if (m_currentQueueItem?.isMandatory == true) return;
            UseCurrent(null);
        }

        public void UseCurrent(object? selectedReward = null) => _finished = true;

        public void UpdateImmediately()
        {
            if (!_finished) return;
            if (m_currentRewardQueneItems.Count == 0)
            {
                IsActive = false;
                return;
            }

            m_currentQueueItem = m_currentRewardQueneItems[0];
            m_currentRewardQueneItems.RemoveAt(0);
            _finished = false;
        }
    }

    internal static class RewardJumpEventHandler
    {
        public static FakeRewardItemType? LastItemType { get; private set; }

        public static void Reset() => LastItemType = null;

        public static void Throw(FakeRewardItemType itemType) => LastItemType = itemType;
    }
}

internal sealed class GuiSaveHandler
{
    public static GuiSaveHandler Instance { get; } = new();
    public static bool WasSaved { get; private set; }

    public static void Reset() => WasSaved = false;

    public void SaveDurationInValidGameTick(string first, string source, int value) => WasSaved = true;
}
