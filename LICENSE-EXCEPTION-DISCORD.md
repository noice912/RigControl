# Additional permission: Discord Social SDK

RigControl is licensed under the GNU General Public License version 3 (see `LICENSE`). Its copyright holders add the
following permission, as allowed by section 7 of that licence.

## Additional permission under GNU GPL version 3 section 7

If you modify this Program, or any covered work, by linking or combining it with Discord's Social SDK (the
`discord_partner_sdk` library, or a modified version of that library), containing parts covered by the terms of
Discord's developer terms and the Social SDK's own licence, the licensors of this Program grant you additional
permission to convey the resulting work. Corresponding Source for a non-source form of such a combination shall not
include the source code for the parts of the Discord Social SDK used, as well as that of the covered work.

## Where it is used

Only RigControl Mobile's iPhone app uses it, for "Sign in with Discord" (`mobile/RigControl.Mobile/Services/DiscordDirect.cs`,
`mobile/RigControl.Mobile/Platforms/iOS/DiscordSdk.cs`). The SDK itself is Discord's software. It is not part of this
repository and is not covered by the GPL: builds that include it get it from Discord (developer.discord.com), under
Discord's terms. Built without it, the app simply doesn't offer the Discord sign-in.
