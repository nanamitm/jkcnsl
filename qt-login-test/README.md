# jkcnsl Qt login helper

This Qt 6 application performs the interactive Niconico browser login for
`jkcnsl`. It does not share a profile with Chrome, Edge, or another browser.

The application creates an off-the-record `QWebEngineProfile`, opens the
Niconico login page, and captures the session-cookie names that `jkcnsl`
needs (`user_session`, `user_session_secure`, and `nicosid`). Cookie values
are never displayed. When launched by `jkcnsl Ai`, values are sent once
through a random local named pipe; `jkcnsl` validates them and saves them with
its existing protection logic. When launched manually, the helper saves the
cookie to the adjacent `jkcnsl.json` using the same Windows DPAPI
LocalMachine protection as `jkcnsl`.

## Build on this machine

Open an `x64 Native Tools Command Prompt for VS 2022`, then run:

```bat
cd /d F:\VTemp\claude\jkcnsl\qt-login-test
C:\Qt\6.11.1\msvc2022_64\bin\qt-cmake.bat -S . -B build -G Ninja
cmake --build build
```

Copy `build\jkcnsl-qt-login.exe` and its Qt deployment files beside
`jkcnsl.exe`. Use `windeployqt` before distributing the helper.

## Test procedure

1. Open the login window and complete the normal Niconico login flow.
2. Complete Turnstile, MFA, or passkey prompts in the page itself.
3. Click **Cookie取得を確認**.
4. When run by `jkcnsl Ai`, click **jkcnslへログインを引き渡す**.

When run manually without `--pipe` and `--nonce`, click
**jkcnsl.jsonへログインを保存** to update the adjacent settings file.
