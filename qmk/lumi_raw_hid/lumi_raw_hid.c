#include "lumi_raw_hid.h"

#include "raw_hid.h"

#include <stdio.h>
#include <string.h>

#if RAW_EPSIZE != 32
#    error "Lumi QMK protocol requires QMK RAW_EPSIZE == 32"
#endif

#define LUMI_MAGIC0 ((uint8_t)'L')
#define LUMI_MAGIC1 ((uint8_t)'Q')
#define LUMI_FRAME_VERSION 1
#define LUMI_FLAG_START 0x01
#define LUMI_FLAG_END 0x02
#define LUMI_FLAG_RESPONSE 0x04
#define LUMI_HEADER_SIZE 7
#define LUMI_PAYLOAD_SIZE (RAW_EPSIZE - LUMI_HEADER_SIZE)

static char rx_buffer[LUMI_QMK_MAX_MESSAGE + 1];
static size_t rx_length;
static uint8_t rx_sequence;
static uint8_t rx_fragment;
static bool rx_active;

__attribute__((weak)) bool lumi_qmk_process_command(
    const char *command,
    char *response,
    size_t response_size) {
    (void)command;
    (void)response;
    (void)response_size;
    return false;
}

static void lumi_send_response(uint8_t sequence, const char *text) {
    const uint8_t *bytes = (const uint8_t *)text;
    size_t length = strlen(text);
    size_t fragments =
        length == 0 ? 1 : (length + LUMI_PAYLOAD_SIZE - 1) / LUMI_PAYLOAD_SIZE;

    for (size_t fragment = 0; fragment < fragments; fragment++) {
        uint8_t report[RAW_EPSIZE] = {0};
        size_t offset = fragment * LUMI_PAYLOAD_SIZE;
        size_t count =
            length > offset ? length - offset : 0;

        if (count > LUMI_PAYLOAD_SIZE) {
            count = LUMI_PAYLOAD_SIZE;
        }

        report[0] = LUMI_MAGIC0;
        report[1] = LUMI_MAGIC1;
        report[2] = LUMI_FRAME_VERSION;
        report[3] = LUMI_FLAG_RESPONSE;

        if (fragment == 0) {
            report[3] |= LUMI_FLAG_START;
        }
        if (fragment == fragments - 1) {
            report[3] |= LUMI_FLAG_END;
        }

        report[4] = sequence;
        report[5] = (uint8_t)fragment;
        report[6] = (uint8_t)count;

        if (count > 0) {
            memcpy(&report[LUMI_HEADER_SIZE], &bytes[offset], count);
        }

        raw_hid_send(report, RAW_EPSIZE);
    }
}

void raw_hid_receive(uint8_t *data, uint8_t length) {
    if (length != RAW_EPSIZE ||
        data[0] != LUMI_MAGIC0 ||
        data[1] != LUMI_MAGIC1 ||
        data[2] != LUMI_FRAME_VERSION) {
        return;
    }

    uint8_t flags = data[3];
    uint8_t sequence = data[4];
    uint8_t fragment = data[5];
    uint8_t count = data[6];

    if ((flags & LUMI_FLAG_RESPONSE) != 0 ||
        count > LUMI_PAYLOAD_SIZE) {
        return;
    }

    if ((flags & LUMI_FLAG_START) != 0) {
        rx_active = true;
        rx_sequence = sequence;
        rx_fragment = 0;
        rx_length = 0;
    }

    if (!rx_active ||
        sequence != rx_sequence ||
        fragment != rx_fragment) {
        rx_active = false;
        return;
    }

    if (rx_length + count > LUMI_QMK_MAX_MESSAGE) {
        rx_active = false;
        return;
    }

    if (count > 0) {
        memcpy(
            &rx_buffer[rx_length],
            &data[LUMI_HEADER_SIZE],
            count);
        rx_length += count;
    }

    rx_fragment++;

    if ((flags & LUMI_FLAG_END) == 0) {
        return;
    }

    rx_active = false;
    rx_buffer[rx_length] = '\0';

    char response[LUMI_QMK_MAX_MESSAGE + 1] = {0};
    bool has_response = false;

    if (strcmp(rx_buffer, "HELLO") == 0) {
        snprintf(
            response,
            sizeof(response),
            "LUMIPAD|3|FW=QMK|CAPS=%s",
            LUMI_QMK_CAPS);
        has_response = true;
    } else {
        has_response =
            lumi_qmk_process_command(
                rx_buffer,
                response,
                sizeof(response));
    }

    if (has_response) {
        response[sizeof(response) - 1] = '\0';
        lumi_send_response(sequence, response);
    }
}
