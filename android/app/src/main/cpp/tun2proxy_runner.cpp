#include <cstdlib>
#include <string>

#include "tun2proxy.h"

int main(int argc, char **argv) {
    unsigned short mtu = 1500;
    std::string arguments = "tun2proxy-bin";

    for (int index = 1; index < argc; ++index) {
        const std::string argument(argv[index]);
        if (argument == "--tun-mtu" && index + 1 < argc) {
            mtu = static_cast<unsigned short>(std::strtoul(argv[++index], nullptr, 10));
            continue;
        }
        arguments += " " + argument;
    }

    return tun2proxy_run_with_cli_args(arguments.c_str(), mtu, false) == 0 ? 0 : 1;
}
