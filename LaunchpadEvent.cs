namespace Launchpad
{
    public enum EventType
    {
        ButtonDown,
        ButtonUp
    }
    public struct LaunchpadEvent
    {
        public EventType Type;
        public byte ButtonId, ButtonX, ButtonY;
        public bool IsSystemButton;

        public LaunchpadEvent(EventType type, byte button)
        {
            Type = type;
            ButtonId = button;
            if (button >= 104)
                button -= 13;
            ButtonY = (byte)(button / 10U);
            ButtonX = (byte)(button % 10U);
            IsSystemButton = ButtonX == 9 || ButtonY == 9;
        }
    }
}
