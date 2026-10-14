#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pty.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

static volatile sig_atomic_t child_pid = -1;
static volatile sig_atomic_t stopping = 0;

static void stop_handler(int signal_number) {
    (void)signal_number;
    stopping = 1;
    if (child_pid > 0) kill(-child_pid, SIGHUP);
}

static int write_all(int fd, const void *buffer, size_t length) {
    const char *p = buffer;
    while (length) {
        ssize_t n = write(fd, p, length);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return -1;
        p += n; length -= (size_t)n;
    }
    return 0;
}

static int terminate_process_group(pid_t child, int status, int child_reaped) {
    // Drain the entire PTY process group with bounded HUP/TERM/KILL escalation.
    for (int phase = 0; phase < 3; phase++) {
        int signal_number = phase == 0 ? SIGHUP : (phase == 1 ? SIGTERM : SIGKILL);
        kill(-child, signal_number);
        for (int attempt = 0; attempt < 10; attempt++) {
            if (!child_reaped) {
                pid_t result_pid = waitpid(child, &status, WNOHANG);
                if (result_pid == child) { child_reaped = 1; child_pid = -1; }
            }
            if (kill(-child, 0) < 0 && errno == ESRCH) break;
            usleep(100000);
        }
    }
    if (!child_reaped) {
        kill(-child, SIGKILL);
        while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
    }
    child_pid = -1;
    return WIFEXITED(status) ? WEXITSTATUS(status) : (WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 1);
}

int main(int argc, char **argv) {
    if (argc < 4 || strncmp(argv[1], "--control=", 10) != 0) return 2;
    const char *control_path = argv[1] + 10;
    struct winsize size = { .ws_row = 30, .ws_col = 120 };
    const char *rows = getenv("HITCH_ROWS"), *cols = getenv("HITCH_COLUMNS");
    if (rows) size.ws_row = (unsigned short)atoi(rows);
    if (cols) size.ws_col = (unsigned short)atoi(cols);
    int master = -1;
    pid_t child = forkpty(&master, NULL, NULL, &size);
    if (child < 0) { perror("forkpty"); return 1; }
    if (child == 0) {
        execvp(argv[2], &argv[2]);
        perror("execvp"); _exit(127);
    }
    child_pid = child;
    struct sigaction action = {0};
    action.sa_handler = stop_handler;
    sigemptyset(&action.sa_mask);
    sigaction(SIGTERM, &action, NULL);
    sigaction(SIGINT, &action, NULL);
    sigaction(SIGHUP, &action, NULL);

    int control = socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    if (control < 0) return terminate_process_group(child, 0, 0);
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    if (strlen(control_path) >= sizeof(address.sun_path)) { close(control); return terminate_process_group(child, 0, 0); }
    strcpy(address.sun_path, control_path);
    unlink(control_path);
    if (bind(control, (struct sockaddr *)&address, sizeof(address)) < 0 || chmod(control_path, 0600) < 0) {
        perror("PTY resize socket"); close(control); return terminate_process_group(child, 0, 0);
    }

    unsigned char buffer[8192];
    struct pollfd fds[3] = {{ .fd = STDIN_FILENO, .events = POLLIN }, { .fd = master, .events = POLLIN }, { .fd = control, .events = POLLIN }};
    int input_open = 1, master_open = 1, status = 0, child_reaped = 0;
    while (master_open && !stopping) {
        fds[0].fd = input_open ? STDIN_FILENO : -1;
        fds[0].events = input_open ? POLLIN : 0;
        fds[1].events = master_open ? POLLIN : 0;
        int result = poll(fds, 3, 100);
        if (result < 0 && errno != EINTR) break;
        if (fds[2].revents & POLLIN) {
            uint16_t dimensions[2];
            ssize_t received = recv(control, dimensions, sizeof(dimensions), 0);
            if (received == (ssize_t)sizeof(dimensions)) {
                struct winsize ws = { .ws_row = dimensions[1], .ws_col = dimensions[0] };
                ioctl(master, TIOCSWINSZ, &ws);
            }
        }
        if (input_open && (fds[0].revents & (POLLIN | POLLHUP))) {
            ssize_t n = read(STDIN_FILENO, buffer, sizeof(buffer));
            if (n <= 0) { input_open = 0; stopping = 1; kill(-child, SIGHUP); }
            else if (write_all(master, buffer, (size_t)n) < 0) input_open = 0;
        }
        if (master_open && (fds[1].revents & (POLLIN | POLLHUP))) {
            ssize_t n = read(master, buffer, sizeof(buffer));
            if (n <= 0) master_open = 0;
            else if (write_all(STDOUT_FILENO, buffer, (size_t)n) < 0) { kill(-child, SIGHUP); master_open = 0; }
        }
        pid_t result_pid = waitpid(child, &status, WNOHANG);
        if (result_pid == child) { child_pid = -1; child_reaped = 1; break; }
    }
    // The shell can exit while background descendants still occupy its PTY.
    status = terminate_process_group(child, status, child_reaped);
    close(master); close(control); unlink(control_path);
    return status;
}
