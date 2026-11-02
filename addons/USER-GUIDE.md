# Using AJN addons

Addons add features to AJN. This Windows developer preview installs `.ajnaddon`
files that a creator gives you. Website installation and automatic addon updates
are planned for later.

You need the **complete AJN addon preview**, extracted into a writable folder.
Open the `AnimeJaNaiManager.exe` inside that folder. Installing and using addons
does not require programming tools, .NET SDK, or a separate server account.

## Install your first addon

1. Open **Addons**. AJN prepares its local addon support automatically.
2. Choose **Install addon…** and select the creator's `.ajnaddon` file.
   Keep the file intact; do not unzip it or copy its contents into AJN.
3. Read the package name, version and requested permissions. Check the access
   you want to allow, then choose **Install**. Nothing is checked for you.
4. The installed addon becomes selected. If it is stopped, choose **Start addon**.
5. Adjust its settings, choose **Save settings**, and use its action buttons.

Some addons start automatically with Manager or a player. Their declared startup
events determine this. After installing your first addon, reopen any video that
was already playing so that player can participate.

Local packages currently have no verified publisher identity. A package's name
alone does not prove who made it. Permission review applies to the exact file
you selected.

## Understand permissions and access

Only checked permissions are allowed. An addon may still install with a
permission unchecked, but a feature needing it may be unavailable. To change
your choices in this preview, use **Install addon…** with the same package again
and review the permissions anew. This restarts that addon and keeps its saved
settings and data.

Some permissions also require a specific resource to be approved:

| What the addon needs | Where you choose what it can use |
| --- | --- |
| Its own saved data | Installation permission review; it cannot use another addon's private data |
| A local video | **Processing access → Allow a media file** |
| Your processing settings | **Processing access → Allow a saved profile**; save profile edits first |
| Images from normal AJN playback | Both image-sample and normal-player-observation permissions during installation |
| A service or device | **Service and device access → Allow a service or device** |
| A saved service credential | The specific approved service's **Save credential** control, plus the credential permission |
| Remote video input or processed video output | Their separate install permissions, an approved service, and an approved processing profile |

Approving a saved profile gives the addon a copy of those settings. Later profile
edits do not silently change the copy. Approve another saved copy when needed.
Removing media/service access stops the addon and its work. Reapprove only the
access you want, then start it again. Changing or removing a saved credential
also stops the addon so old access cannot continue unnoticed.

## Start, stop, save and close

- **Start addon** starts it manually. It can keep working after Manager closes.
  Choosing Start on an already running compatible addon also keeps it running
  independently of Manager. **Stop addon** ends its current work.
- Actions are available while the addon is running. This lets multi-step actions
  retain their sessions and observations between clicks.
- **Save settings** stores your edits. Switching addons will remind you to save
  first. **Reload** lets you explicitly discard unsaved changes. Closing Manager
  also asks before discarding unsaved addon settings; Cancel keeps your draft.
- Stopping an addon does not uninstall it. A later declared startup event, such
  as opening another player, can start it again. Remove it to prevent future use.
- A stopped or failed addon is not repeatedly restarted by a timer. Read its
  message and choose Start when you are ready to retry.

**Performance limits** controls how many background video-processing sessions
all addons can run together: 1–16, default 2. Raising the limit allows more work
and can increase GPU/memory use. Lowering it leaves existing sessions running
and limits new ones. An addon's own stream-count setting may also need adjustment.
This limit is separate from ordinary player observations and worker limits.

**Windows startup** is optional and off by default. Enable it only if you want
addons that support sign-in activation to start when you sign in. Turning it off
ends sign-in activation; addons started manually or in use by a player can remain
running. The setting applies to your Windows account and this AJN installation.

## Update, restore or remove

Install the newer `.ajnaddon` file to update an addon. It should use the same
addon ID. Review the new package and permissions; settings and private data are
retained. Check the access sections too: resource approvals are tied to the exact
package version and may need review again.

**Previous version** asks before restoring the last installed package and its
previous permission choices. Saved settings/data are kept, so a creator must make
data changes compatible with rollback. If a saved setting is incompatible,
Manager explains which field needs attention. Review its displayed default and
save before starting the addon.

**Remove** asks before stopping the addon and removing its registration and
resource access. Saved settings and private data are kept for reinstallation.
It is not a “delete all data” operation.

## If something goes wrong

Read the message at the top of Addons. **Try again** reconnects local addon
support after a failure. AJN keeps installed packages and saved settings; it does
not repeat a failed install or action for you. Review the resulting state before
trying that operation again.

If support files are missing, extract the complete preview into a new folder
and use its Manager. Replacing just the Manager executable is insufficient.
If an addon stops, read **Recent addon messages**. Expand **Technical details**
when reporting a problem to its creator.

Include the AJN preview version, addon name/version, the action you took, and
the error text. Include GPU/backend and source format for video problems. Do not
include service credentials or private media URLs in a public report.

See [Troubleshooting](TROUBLESHOOTING.md) for specific recovery steps.
[Creator guide](CREATOR-GUIDE.md) explains how to build an addon.
