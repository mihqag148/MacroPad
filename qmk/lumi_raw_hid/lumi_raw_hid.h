#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifndef LUMI_QMK_CAPS
#    define LUMI_QMK_CAPS ""
#endif

#ifndef LUMI_QMK_MAX_MESSAGE
#    define LUMI_QMK_MAX_MESSAGE 768
#endif

/*
 * Called after a complete Lumi command has been reassembled.
 *
 * Return true when a response should be sent back to the app and write a
 * NUL-terminated response into response. Return false for fire-and-forget
 * commands.
 */
bool lumi_qmk_process_command(
    const char *command,
    char *response,
    size_t response_size);
