#include <jni.h>

#include <cerrno>
#include <csignal>
#include <string>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

namespace {
constexpr int CHILD_TUN_FD = 198;
}

extern "C" JNIEXPORT jint JNICALL
Java_io_tcproute_android_Tun2ProxyBridge_start(
    JNIEnv *env,
    jobject,
    jstring raw_runner_path,
    jint tun_fd,
    jint socks_port,
    jint mtu
) {
    const char *runner_chars = env->GetStringUTFChars(raw_runner_path, nullptr);
    if (runner_chars == nullptr) {
        return -ENOMEM;
    }
    const std::string runner_path(runner_chars);
    env->ReleaseStringUTFChars(raw_runner_path, runner_chars);

    const std::string tun_fd_value = std::to_string(CHILD_TUN_FD);
    const std::string proxy_value = "socks5://127.0.0.1:" + std::to_string(socks_port);
    const std::string mtu_value = std::to_string(mtu);
    char *const arguments[] = {
        const_cast<char *>(runner_path.c_str()),
        const_cast<char *>("--tun-fd"),
        const_cast<char *>(tun_fd_value.c_str()),
        const_cast<char *>("--close-fd-on-drop"),
        const_cast<char *>("true"),
        const_cast<char *>("--proxy"),
        const_cast<char *>(proxy_value.c_str()),
        const_cast<char *>("--dns"),
        const_cast<char *>("over-tcp"),
        const_cast<char *>("--verbosity"),
        const_cast<char *>("info"),
        const_cast<char *>("--exit-on-fatal-error"),
        const_cast<char *>("--tun-mtu"),
        const_cast<char *>(mtu_value.c_str()),
        nullptr
    };

    const pid_t pid = fork();
    if (pid < 0) {
        return -errno;
    }
    if (pid == 0) {
        if (dup2(tun_fd, CHILD_TUN_FD) < 0) {
            _exit(126);
        }
        execve(runner_path.c_str(), arguments, environ);
        _exit(127);
    }
    return pid;
}

extern "C" JNIEXPORT jint JNICALL
Java_io_tcproute_android_Tun2ProxyBridge_stop(JNIEnv *, jobject, jint raw_pid) {
    const pid_t pid = static_cast<pid_t>(raw_pid);
    if (pid <= 0) {
        return -EINVAL;
    }

    if (kill(pid, SIGTERM) < 0 && errno != ESRCH) {
        return -errno;
    }

    int status = 0;
    for (int attempt = 0; attempt < 50; ++attempt) {
        const pid_t result = waitpid(pid, &status, WNOHANG);
        if (result == pid || (result < 0 && errno == ECHILD)) {
            return 0;
        }
        usleep(10000);
    }

    kill(pid, SIGKILL);
    while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {
    }
    return 0;
}

extern "C" JNIEXPORT jint JNICALL
Java_io_tcproute_android_Tun2ProxyBridge_poll(JNIEnv *, jobject, jint raw_pid) {
    const pid_t pid = static_cast<pid_t>(raw_pid);
    if (pid <= 0) {
        return -EINVAL;
    }

    int status = 0;
    const pid_t result = waitpid(pid, &status, WNOHANG);
    if (result == 0) {
        return 0;
    }
    if (result == pid || (result < 0 && errno == ECHILD)) {
        return 1;
    }
    return -errno;
}

extern "C" JNIEXPORT jint JNICALL
Java_io_tcproute_android_Tun2ProxyBridge_probe(JNIEnv *, jobject) {
    return 1;
}
