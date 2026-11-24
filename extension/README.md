# RigControl browser extension

Tells RigControl which show, episode and position you're watching on Netflix, Hulu, Disney+, Prime Video, Max,
Crunchyroll, Paramount+, Peacock, Apple TV+, Plex, YouTube and Twitch, so the Discord "Watching" card can show them.
It only talks to this PC (`http://127.0.0.1`, ports 47610 to 47619: each Windows user's RigControl has its own, and
the extension finds it by itself), reads nothing but the player's title, episode and position, and sends nothing
anywhere else.

Which copy to use: RigControl's Games page has a button, **Get the browser extension**, that puts a copy of this folder
on your PC. That copy carries a secret of your own (`link-token.js`) and only reports to an app that proves it is your
RigControl. Loaded straight from this folder there is no secret, so the extension cannot check: it reports to the first
app on those ports that calls itself RigControl. On a PC that other people use too, use the copy from the button.
(The copy with a secret does not report to RichPresence, which cannot prove anything.)

Install (Chrome or Edge): open `chrome://extensions` or `edge://extensions`, turn on Developer mode, press
**Load unpacked** and pick this folder. RigControl's Games page has a button that puts a copy of this folder on
your PC.

Brought over from RichPresence's extension (MIT, see `LICENSE-RichPresence.txt`); same protocol, so it works
with both apps.
