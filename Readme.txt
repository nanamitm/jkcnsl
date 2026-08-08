jkcnsl

## Overview

jkcnsl is an unofficial command-line tool mainly intended for retrieving comments from Niconico Jikkyo.

## Disclaimer

This is an unofficial tool. Changes to Niconico Jikkyo (specific channels on https://live.nicovideo.jp/) or other factors may cause failures or other disadvantages.

The source code is provided as-is. Please inspect and build it at your own responsibility.

## Building and usage

This is a .NET application. In a shell, change to the project directory and build it, for example:

> dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true

Windows 10 or later is expected to be supported.

On Linux, build it as follows (Ubuntu 24.04 example). The default location for the configuration file and related data is `/var/local/jkcnsl`.

> sudo apt install dotnet-sdk-10.0
> dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true
> sudo install ./bin/Release/net10.0/linux-x64/publish/jkcnsl /usr/local/bin
> sudo mkdir /var/local/jkcnsl
> sudo chown $USER /var/local/jkcnsl  # Adjust permissions as appropriate.

A Windows build targeting `linux-x64` should also run on Linux. Use `linux-arm64` for ARM systems.

Start jkcnsl and enter a command such as:

> Lch???<Enter> (replace `???` with the Jikkyo channel number)

Comments retrieved from Niconico Jikkyo will then be printed. Enter `c<Enter>` or `q<Enter>` to exit.

To connect to a volunteer-operated refuge server, use:

> R1 wss://{volunteer viewing-session address}<Enter>

### Signing in to Niconico Jikkyo

Run the following command:

> Ai<Enter>

On Windows, this launches the bundled `jkcnsl_login/jkcnsl-qt-login.exe` helper. Sign in normally in its browser window, then use the helper's button to return the login cookie to jkcnsl. A `.` indicates success and `!` indicates failure.

When the helper is started by itself, it can also save the login cookie directly to `jkcnsl.json`. Keep the `jkcnsl_login` directory beside `jkcnsl.exe`; it contains the Qt WebEngine files required by the browser helper.

To sign out, enter:

> Ao<Enter>

When login information is configured, jkcnsl attempts to sign in automatically on the first connection to Niconico Jikkyo. If you no longer need it, sign out and remove the login settings with:

> Smail<Enter>
> Spassword<Enter>

Removing either setting is sufficient. Deleting `jkcnsl.json` also removes the saved login information.

Enter `S<Enter>` to print all current settings.

## Command-line options

  -d {directory}     Specifies where `jkcnsl.json` is read and written.
                     If omitted, the directory containing `jkcnsl.exe` is used.
                     Specify a separate directory for each user when multiple
                     users use their own accounts.
  -c {command}       One-shot command mode. Runs the specified command and exits.
  -n {pipe name}     Receives input through a named pipe instead of standard input.
  -i                 Outputs the Jikkyo stream intermittently.
  -p {PID}           Monitors the specified process and exits when it terminates
                     (for NicoJK).
  --post-as-anon     Posts comments anonymously.
  --post-drop-dup    Rejects a comment identical to the immediately previous post.

## License

MIT.

## Source

https://github.com/xtne6f/jkcnsl

Trace output is suppressed outside Windows. Build with `-p:AdditionalConstants=DO_NOT_SUPPRESS_TRACE` to disable that suppression.

The files under the `dwango` and `google` directories were generated with protogen 3.2.52 from the `.proto` files in:

https://github.com/n-air-app/nicolive-comment-protobuf/tree/871fe37c088af7e34fffd93aa7c2c309be5d90d2
https://github.com/protocolbuffers/protobuf/tree/35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03

using the following PowerShell commands:

> ls dwango\nicolive\chat\data\*.proto, dwango\nicolive\chat\data\atoms\*.proto, dwango\nicolive\chat\service\edge\payload.proto | Resolve-Path -Relative | %{ protogen --csharp_out=. +names=original "$_" }
> ls google\protobuf\struct.proto | Resolve-Path -Relative | %{ protogen --csharp_out=. +names=original "$_" }

## Acknowledgements

The implementation was especially informed by https://github.com/tsukumijima/TVRemotePlus and https://github.com/asannou/namami. In particular, many variable names and implementation ideas were borrowed from TVRemotePlus.

Support for the post-2024 Niconico Jikkyo system was especially informed by https://github.com/tsukumijima/NDGRClient and https://github.com/noriokun4649/TVTComment.

The original login implementation was informed by nicologin (www.axfc.net/u/4052467).

## Connecting through a cache server (custom addition)

When `cache_server_url` is configured, connections to Niconico Jikkyo made by the `L` command are routed through a cache server. When multiple clients watch the same channel, the cache server consolidates their upstream connections and distributes comments to them.

To configure the cache server URL:

> Scache_server_url wss://{cache server address}<Enter>

To clear the setting and return to direct Niconico Jikkyo connections:

> Scache_server_url<Enter>

When `cache_server_url` is set, `Lch???` connects to `/watch/ch???` on the cache server. When it is not set, jkcnsl connects directly to Niconico Jikkyo as before.

The `R` command for refuge servers such as NX-Jikkyo does not require a jkcnsl modification to use the cache server. Specify the destination as follows:

> R1 wss://{cache server address}/watch/{channel name}<Enter>

For NicoJK, change `refugeUri` in `NicoJK.ini` to:

> refugeUri=wss://{cache server address}/watch/{jkID}

When `refugeMixing=1` is configured in `NicoJK.ini`, setting `cache_server_url` routes both the refuge server and Niconico Jikkyo through the cache server. Duplicate Niconico Jikkyo comments forwarded from the refuge server are removed automatically.

For configuration-file placement and the `jkcnsl.json` format, see `setting/readme.txt`.
