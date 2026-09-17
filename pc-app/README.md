# LumiPad Windows App

Windows companion app for the Lumi MacroPad.

Current functions:

- Reads the Windows Now Playing / SMTC session from supported players.
- Sends title, artist, play/pause state, elapsed time and duration to the macropad.
- Shows an iPod-inspired Now Playing screen on the 320x172 ST7789.
- Controls the four WS2812B LEDs:
  - Auto by Layer
  - Rainbow
  - Warm Purple Ping-Pong
  - Orange Blink
  - Solid Color
  - LED On/Off
  - Brightness 5-50%
- Uses a second USB CDC serial interface, so ZMK Studio keeps its own USB serial connection.

## Use

1. Flash the matching newest Lumi MacroPad firmware.
2. Connect the macropad to the Windows PC by USB.
3. Open LumiPad.exe.
4. Press Detect LumiPad if it is not detected automatically.
5. Start playing music.

Advanced Now Playing functions require the Windows app to stay running. Normal ZMK keyboard/BLE functionality does not.
