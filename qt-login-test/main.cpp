#include <QApplication>
#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QHash>
#include <QHBoxLayout>
#include <QIcon>
#include <QLabel>
#include <QMainWindow>
#include <QMessageBox>
#include <QNetworkCookie>
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonParseError>
#include <QPushButton>
#include <QStatusBar>
#include <QStringList>
#include <QThread>
#include <QUrl>
#include <QVBoxLayout>
#include <QWebEngineCookieStore>
#include <QWebEnginePage>
#include <QWebEngineProfile>
#include <QWebEngineView>
#include <QWidget>

#ifdef Q_OS_WIN
#include <windows.h>
#include <wincrypt.h>
#endif

namespace
{
constexpr auto LoginUrl = "https://account.nicovideo.jp/login?site=niconico";
constexpr auto AccountUrl = "https://account.nicovideo.jp/my/account";

bool IsNiconicoDomain(const QString &domain)
{
    const QString lowerDomain = domain.toLower();
    return lowerDomain == "nicovideo.jp" || lowerDomain.endsWith(".nicovideo.jp");
}

#ifdef Q_OS_WIN
QString ProtectForJkcnslSettings(const QString &value, QString *error)
{
    QByteArray utf8 = value.toUtf8();
    DATA_BLOB input{ static_cast<DWORD>(utf8.size()), reinterpret_cast<BYTE *>(utf8.data()) };
    DATA_BLOB output{};
    if (!CryptProtectData(&input, L"jkcnsl nicovideo cookie", nullptr, nullptr, nullptr,
                          CRYPTPROTECT_LOCAL_MACHINE, &output))
    {
        *error = QObject::tr("Cookieの暗号化に失敗しました（Windows エラー %1）。").arg(GetLastError());
        return {};
    }

    const QByteArray encrypted(reinterpret_cast<const char *>(output.pbData), static_cast<int>(output.cbData));
    LocalFree(output.pbData);
    return QString::fromLatin1(encrypted.toHex().toUpper());
}
#endif

class LoginWindow final : public QMainWindow
{
public:
    LoginWindow(QString pipeName, QByteArray nonce, QString settingsPath)
        : pipeName_(std::move(pipeName)), nonce_(std::move(nonce)), settingsPath_(std::move(settingsPath)), profile_(this)
    {
        setWindowTitle(tr("jkcnsl - ニコニコログイン"));
        resize(1120, 820);
        profile_.setHttpCacheType(QWebEngineProfile::MemoryHttpCache);

        auto *page = new QWebEnginePage(&profile_, &view_);
        view_.setPage(page);
        auto *central = new QWidget(this);
        auto *layout = new QVBoxLayout(central);
        auto *note = new QLabel(tr("このウィンドウ内で通常どおりログインしてください。"
                                   "Cookie値は画面へ表示せず、jkcnsl起動時だけ名前付きパイプで渡されます。"), central);
        note->setWordWrap(true);
        layout->addWidget(note);
        layout->addWidget(&view_, 1);

        auto *buttons = new QHBoxLayout();
        auto *openLoginButton = new QPushButton(tr("ログイン画面を開く"), central);
        auto *checkButton = new QPushButton(tr("Cookie取得を確認"), central);
        handoffButton_ = new QPushButton(pipeName_.isEmpty() ? tr("jkcnsl.jsonへログインを保存")
                                                             : tr("jkcnslへログインを引き渡す"), central);
        auto *closeButton = new QPushButton(tr("破棄して閉じる"), central);
        handoffButton_->setEnabled(pipeName_.isEmpty() || !nonce_.isEmpty());
        buttons->addWidget(openLoginButton);
        buttons->addWidget(checkButton);
        buttons->addWidget(handoffButton_);
        buttons->addStretch(1);
        buttons->addWidget(closeButton);
        layout->addLayout(buttons);
        setCentralWidget(central);

        connect(openLoginButton, &QPushButton::clicked, this, [this] { view_.load(QUrl(QString::fromLatin1(LoginUrl))); });
        connect(checkButton, &QPushButton::clicked, this, [this] { ShowCookieStatus(); });
        connect(handoffButton_, &QPushButton::clicked, this, [this] { SaveOrHandoffToJkcnsl(); });
        connect(closeButton, &QPushButton::clicked, this, [this] { DiscardAndClose(); });
        connect(profile_.cookieStore(), &QWebEngineCookieStore::cookieAdded, this,
                [this](const QNetworkCookie &cookie) { RecordCookie(cookie); });
        connect(&view_, &QWebEngineView::urlChanged, this, [this](const QUrl &url) { statusBar()->showMessage(url.toDisplayString()); });

        profile_.cookieStore()->loadAllCookies();
        view_.load(QUrl(QString::fromLatin1(LoginUrl)));
    }

private:
    bool HasUserSession() const
    {
        return observedCookies_.contains("user_session") || observedCookies_.contains("user_session_secure");
    }

    void RecordCookie(const QNetworkCookie &cookie)
    {
        if (!IsNiconicoDomain(cookie.domain()))
            return;
        const QString name = QString::fromLatin1(cookie.name());
        if (name == "nicosid" || name == "user_session" || name == "user_session_secure")
        {
            // 値は画面やログへ出さず、引き渡し時だけメモリから読む。
            observedCookies_.insert(name, cookie);
            statusBar()->showMessage(tr("ニコニコのセッションCookieを検出しました（値は表示しません）。"));
        }
    }

    void ShowCookieStatus()
    {
        const bool hasUserSession = HasUserSession();
        const QString result = tr("検出結果（Cookie値は表示しません）\n\n"
                                  "user_session / user_session_secure: %1\n"
                                  "nicosid: %2\n\n"
                                  "%3")
                                   .arg(hasUserSession ? tr("あり") : tr("なし"))
                                   .arg(observedCookies_.contains("nicosid") ? tr("あり") : tr("なし"))
                                   .arg(hasUserSession ? tr("セッションCookieを取得できています。")
                                                       : tr("ログイン完了後にもう一度確認してください。"));
        QMessageBox::information(this, tr("ログイン状態"), result);
        if (hasUserSession)
            view_.load(QUrl(QString::fromLatin1(AccountUrl)));
    }

    void SaveOrHandoffToJkcnsl()
    {
        if (!HasUserSession())
        {
            QMessageBox::warning(this, tr("ログイン状態"), tr("先にニコニコへログインしてから実行してください。"));
            return;
        }

        QStringList cookieParts;
        for (const auto *name : { "nicosid", "user_session", "user_session_secure" })
        {
            const auto it = observedCookies_.constFind(QString::fromLatin1(name));
            if (it != observedCookies_.cend() && !it->value().isEmpty())
                cookieParts.append(it.key() + "=" + QString::fromLatin1(it->value()));
        }

        const QString cookieHeader = cookieParts.join("; ");
        if (pipeName_.isEmpty())
        {
            SaveCookieToJkcnslSettings(cookieHeader);
            return;
        }

        const QByteArray message = nonce_ + '\n' + cookieHeader.toUtf8().toBase64() + '\n';
#ifdef Q_OS_WIN
        const QString fullPipeName = QStringLiteral("\\\\.\\pipe\\") + pipeName_;
        const auto openPipe = [&fullPipeName]() {
            return CreateFileW(reinterpret_cast<LPCWSTR>(fullPipeName.utf16()), GENERIC_WRITE, 0, nullptr,
                               OPEN_EXISTING, 0, nullptr);
        };
        HANDLE pipe = openPipe();
        if (pipe == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY)
        {
            WaitNamedPipeW(reinterpret_cast<LPCWSTR>(fullPipeName.utf16()), 5000);
            pipe = openPipe();
        }
        if (pipe == INVALID_HANDLE_VALUE)
        {
            QMessageBox::critical(this, tr("引き渡し失敗"),
                                  tr("jkcnslとの安全な接続を確立できませんでした（Windows エラー %1）。")
                                      .arg(GetLastError()));
            return;
        }

        DWORD bytesWritten = 0;
        const BOOL writeSucceeded = WriteFile(pipe, message.constData(), static_cast<DWORD>(message.size()),
                                               &bytesWritten, nullptr);
        const DWORD writeError = writeSucceeded ? ERROR_SUCCESS : GetLastError();
        CloseHandle(pipe);
        if (!writeSucceeded || bytesWritten != static_cast<DWORD>(message.size()))
        {
            QMessageBox::critical(this, tr("引き渡し失敗"),
                                  tr("ログイン情報をjkcnslへ渡せませんでした（Windows エラー %1）。")
                                      .arg(writeError));
            return;
        }
#else
        QMessageBox::critical(this, tr("引き渡し失敗"), tr("このビルドではWindows名前付きパイプを利用できません。"));
        return;
#endif
        QMessageBox::information(this, tr("引き渡し完了"), tr("jkcnslがログイン状態を検証します。"));
        DiscardAndClose();
    }

    void SaveCookieToJkcnslSettings(const QString &cookieHeader)
    {
#ifdef Q_OS_WIN
        QString error;
        const QString protectedCookie = ProtectForJkcnslSettings(cookieHeader, &error);
        if (protectedCookie.isEmpty())
        {
            QMessageBox::critical(this, tr("保存失敗"), error);
            return;
        }

        const QString settingsPath = FindSettingsPath();
        QString saveError;
        for (int retry = 1; retry <= 3; ++retry)
        {
            QJsonObject settings;
            QFile input(settingsPath);
            if (input.exists())
            {
                if (!input.open(QIODevice::ReadOnly))
                {
                    saveError = input.errorString();
                    QThread::msleep(static_cast<unsigned long>(retry * 10));
                    continue;
                }
                QJsonParseError parseError{};
                const QJsonDocument document = QJsonDocument::fromJson(input.readAll(), &parseError);
                if (parseError.error != QJsonParseError::NoError || !document.isObject())
                {
                    QMessageBox::critical(this, tr("保存失敗"),
                                          tr("既存のjkcnsl.jsonを読み取れませんでした。\n%1")
                                              .arg(parseError.errorString()));
                    return;
                }
                settings = document.object();
            }

            settings.insert(QStringLiteral("nicovideo_cookie"), protectedCookie);
            settings.insert(QStringLiteral("nicovideo_mfa_cookie"), QJsonValue::Null);
            settings.insert(QStringLiteral("last_login_attempt"),
                            static_cast<double>(QDateTime::currentSecsSinceEpoch()));

            // F: のようなリムーバブル/仮想ドライブでは QSaveFile の置換が
            // ERROR_ACCESS_DENIED になることがあるため、jkcnsl 本体と同じ直接更新を行う。
            QFile output(settingsPath);
            const QByteArray json = QJsonDocument(settings).toJson();
            if (output.open(QIODevice::WriteOnly | QIODevice::Truncate) &&
                output.write(json) == json.size() && output.flush())
            {
                output.close();
                QMessageBox::information(this, tr("保存完了"), tr("ログイン情報をjkcnsl.jsonへ保存しました。"));
                DiscardAndClose();
                return;
            }
            saveError = output.errorString();
            QThread::msleep(static_cast<unsigned long>(retry * 10));
        }
        QMessageBox::critical(this, tr("保存失敗"),
                              tr("jkcnsl.jsonへ安全に保存できませんでした。\n\n対象: %1\n詳細: %2")
                                  .arg(settingsPath, saveError));
#else
        QMessageBox::critical(this, tr("保存失敗"), tr("jkcnsl.jsonへの暗号化保存はWindowsでのみ利用できます。"));
#endif
    }

    void DiscardAndClose()
    {
        profile_.cookieStore()->deleteAllCookies();
        close();
    }

    QString FindSettingsPath() const
    {
        if (!settingsPath_.isEmpty())
            return settingsPath_;

        const QDir applicationDir(QCoreApplication::applicationDirPath());
        const QString localPath = applicationDir.filePath(QStringLiteral("jkcnsl.json"));
        if (QFileInfo::exists(localPath))
            return localPath;

        const QString parentPath = QDir::cleanPath(applicationDir.filePath(QStringLiteral("../jkcnsl.json")));
        if (QFileInfo::exists(parentPath))
            return parentPath;

        // jkcnsl_login 配置では親フォルダを標準の保存先にする。
        return parentPath;
    }

    QString pipeName_;
    QByteArray nonce_;
    QString settingsPath_;
    QWebEngineProfile profile_;
    QWebEngineView view_;
    QPushButton *handoffButton_ = nullptr;
    QHash<QString, QNetworkCookie> observedCookies_;
};
} // namespace

int main(int argc, char *argv[])
{
    QString pipeName;
    QByteArray nonce;
    QString settingsPath;
    for (int i = 1; i + 1 < argc; i++)
    {
        const QString argument = QString::fromLocal8Bit(argv[i]);
        if (argument == "--pipe")
            pipeName = QString::fromLocal8Bit(argv[++i]);
        else if (argument == "--nonce")
            nonce = QByteArray(argv[++i]);
        else if (argument == "--settings")
            settingsPath = QString::fromLocal8Bit(argv[++i]);
    }

    QApplication app(argc, argv);
    const QIcon appIcon(QStringLiteral(":/jkcnsl-login-icon.png"));
    app.setWindowIcon(appIcon);
    LoginWindow window(pipeName, nonce, settingsPath);
    // Qtの既定値に頼らず、認証ウィンドウ自身にも明示的に設定する。
    window.setWindowIcon(appIcon);
    window.show();
    return app.exec();
}
