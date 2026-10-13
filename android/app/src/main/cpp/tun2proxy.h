#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

int tun2proxy_run_with_cli_args(
    const char *cli_args,
    unsigned short tun_mtu,
    bool packet_information
);

int tun2proxy_stop(void);

#ifdef __cplusplus
}
#endif
