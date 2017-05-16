namespace Launchpad
{
    public enum LEDMode : byte
    {
        Off,
        Normal,
        Flash,
        Pulse
    }

    public struct LED
    {
        public LEDMode Mode;
        public byte Color;
        public byte FlashColor;
    }
}
