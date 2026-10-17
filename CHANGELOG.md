# Changelog

## Unreleased

- The game is now paused for as long as the overlay is open, not only while the achievement list is.
- Exit Game now kills the game process tree immediately instead of waiting for a graceful close.
- Recent-achievement panel has a minimum height so it is no longer cut off.
- New Audio button under Show Playnite, with an in-overlay Audio panel (Windows volume, game volume, mute, output device, and a Media tab for other apps). It needs the Audio Switcher extension; without it the button is hidden.
- Fixed the D-pad and stick skipping a row in the achievement list and the Audio panel (the controller press was also arriving as an arrow key).
- On the main overlay, A and B now act when you let go of the button, so the game no longer receives the A or B press that closed the overlay.
- The Captures button is now the Screenshots button, with a picture icon. When the Screenshots Visualizer extension is installed it opens an in-overlay screenshot gallery (card grid and full-size viewer, controller, keyboard and mouse); Y or R rescans the game's screenshot folders. Without Screenshots Visualizer it opens the captures folder as before.
- The Screenshot button is now called Capture.

## 1.0.0 - 2026-10-09

First public release of Playnite Next Overlay, a fork of Playnite Game Overlay 0.4.5.

- New extension id, name, and assembly, so it can be installed beside the original.
- Screenshot, record, recent-clip, instant-replay, and performance buttons. A fresh install sends the NVIDIA App's standard keys (Alt+F1, Alt+F9, Alt+F10, Alt+Shift+F10, Alt+R).
- Stream button, shown only after you set a shortcut. Clearing any shortcut hides its button.
- Captures button. An empty folder opens your Videos folder, where the NVIDIA App saves its captures.
- Browser button. An empty path opens the default browser, and a browser with only background processes is started instead of being treated as already open.
- Achievements from Playnite Achievements (SQLite cache, read only), with SuccessStory as a fallback.
- Clickable recent-achievement panel and a full list: mouse and gamepad, filters, rarity, secret descriptions hidden until you reveal them.
- Optional pause of the game while the list is open, with several ways back to a running game.
- Discord button removed.
- No personal paths are shipped.
