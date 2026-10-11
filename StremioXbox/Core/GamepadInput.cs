using System;
using System.Linq;
using Windows.Gaming.Input;
using Windows.UI.Xaml;

namespace StremioXbox.Core
{
    /// <summary>Controller buttons the app reacts to. B is left to the system back event.</summary>
    public enum PadButton
    {
        Up,
        Down,
        Left,
        Right,
        A,
        X,
        Y,
        View,
        Menu,
        LeftShoulder,
        RightShoulder,
        LeftStick, // Stick click: toggles the app's cursor.
    }

    /// <summary>
    /// Polls the first connected controller (independent of which XAML element has focus)
    /// and raises <see cref="Pressed"/> on button-down, with auto-repeat for held directions.
    /// </summary>
    public sealed class GamepadInput
    {
        private const double StickThreshold = 0.6;
        private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        private uint _previous;
        private PadButton? _held;
        private DateTime _nextRepeat;

        public GamepadInput()
        {
            _timer.Tick += (sender, e) => Poll();
        }

        /// <summary>Raised on the UI thread when a button is pressed (or a held direction repeats).</summary>
        public event Action<PadButton> Pressed;

        /// <summary>Raised on every poll (~60 Hz) with the raw reading, for the app's cursor.</summary>
        public event Action<GamepadReading> Polled;

        /// <summary>When true the left stick no longer acts as a D-pad (it moves the cursor instead).</summary>
        public bool StickIsCursor { get; set; }

        public void Start()
        {
            _timer.Start();
        }

        private static bool IsDirection(PadButton button)
        {
            return button <= PadButton.Right;
        }

        private void Poll()
        {
            var pad = Gamepad.Gamepads.FirstOrDefault();
            if (pad == null)
            {
                _previous = 0;
                _held = null;
                return;
            }

            var reading = pad.GetCurrentReading();
            var buttons = reading.Buttons;
            var stickX = StickIsCursor ? 0 : reading.LeftThumbstickX;
            var stickY = StickIsCursor ? 0 : reading.LeftThumbstickY;
            uint state = 0;

            void Set(PadButton button, bool on)
            {
                if (on)
                {
                    state |= 1u << (int)button;
                }
            }

            Set(PadButton.Up, (buttons & GamepadButtons.DPadUp) != 0 || stickY > StickThreshold);
            Set(PadButton.Down, (buttons & GamepadButtons.DPadDown) != 0 || stickY < -StickThreshold);
            Set(PadButton.Left, (buttons & GamepadButtons.DPadLeft) != 0 || stickX < -StickThreshold);
            Set(PadButton.Right, (buttons & GamepadButtons.DPadRight) != 0 || stickX > StickThreshold);
            Set(PadButton.A, (buttons & GamepadButtons.A) != 0);
            Set(PadButton.X, (buttons & GamepadButtons.X) != 0);
            Set(PadButton.Y, (buttons & GamepadButtons.Y) != 0);
            Set(PadButton.View, (buttons & GamepadButtons.View) != 0);
            Set(PadButton.Menu, (buttons & GamepadButtons.Menu) != 0);
            Set(PadButton.LeftShoulder, (buttons & GamepadButtons.LeftShoulder) != 0);
            Set(PadButton.RightShoulder, (buttons & GamepadButtons.RightShoulder) != 0);
            Set(PadButton.LeftStick, (buttons & GamepadButtons.LeftThumbstick) != 0);

            var now = DateTime.UtcNow;
            var newlyPressed = state & ~_previous;
            _previous = state;

            foreach (PadButton button in Enum.GetValues(typeof(PadButton)))
            {
                if ((newlyPressed & (1u << (int)button)) == 0)
                {
                    continue;
                }

                if (IsDirection(button))
                {
                    _held = button;
                    _nextRepeat = now + RepeatDelay;
                }

                Pressed?.Invoke(button);
            }

            if (_held.HasValue)
            {
                if ((state & (1u << (int)_held.Value)) == 0)
                {
                    _held = null;
                }
                else if (now >= _nextRepeat)
                {
                    _nextRepeat = now + RepeatInterval;
                    Pressed?.Invoke(_held.Value);
                }
            }

            Polled?.Invoke(reading);
        }
    }
}
