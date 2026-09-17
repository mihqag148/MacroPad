/* SPDX-License-Identifier: MIT */

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/uart.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>

#include "lumi_now_playing.h"
#include "lumi_rgb.h"

LOG_MODULE_REGISTER(lumi_app, CONFIG_ZMK_LOG_LEVEL);

#define APP_UART_NODE DT_NODELABEL(lumi_app_uart)
#define LINE_MAX 256

static const struct device *const app_uart = DEVICE_DT_GET(APP_UART_NODE);

static int hex_nibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    c = (char)tolower((unsigned char)c);
    if (c >= 'a' && c <= 'f') return 10 + c - 'a';
    return -1;
}

static void url_decode(char *s) {
    char *src = s;
    char *dst = s;

    while (*src) {
        if (*src == '%' && src[1] && src[2]) {
            int hi = hex_nibble(src[1]);
            int lo = hex_nibble(src[2]);
            if (hi >= 0 && lo >= 0) {
                *dst++ = (char)((hi << 4) | lo);
                src += 3;
                continue;
            }
        }

        if (*src == '+') {
            *dst++ = ' ';
        } else {
            *dst++ = *src;
        }
        src++;
    }

    *dst = '\0';
}

static void write_text(const char *s) {
    while (*s) {
        uart_poll_out(app_uart, (unsigned char)*s++);
    }
}

static void handle_np(char *save) {
    char *pos_s = strtok_r(NULL, "|", &save);
    char *dur_s = strtok_r(NULL, "|", &save);
    char *playing_s = strtok_r(NULL, "|", &save);
    char *title = strtok_r(NULL, "|", &save);
    char *artist = strtok_r(NULL, "|", &save);

    if (!pos_s || !dur_s || !playing_s || !title || !artist) {
        return;
    }

    url_decode(title);
    url_decode(artist);

    lumi_now_playing_update(
        title,
        artist,
        (uint32_t)strtoul(pos_s, NULL, 10),
        (uint32_t)strtoul(dur_s, NULL, 10),
        atoi(playing_s) != 0
    );
}

static void handle_rgb(char *save) {
    char *cmd = strtok_r(NULL, "|", &save);
    if (!cmd) return;

    if (strcmp(cmd, "EN") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_enabled(atoi(v) != 0);
    } else if (strcmp(cmd, "BRI") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_brightness_percent((uint8_t)atoi(v));
    } else if (strcmp(cmd, "AUTO") == 0) {
        lumi_rgb_set_auto(true);
    } else if (strcmp(cmd, "FX") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_effect((uint8_t)atoi(v));
    } else if (strcmp(cmd, "SOLID") == 0) {
        char *r = strtok_r(NULL, "|", &save);
        char *g = strtok_r(NULL, "|", &save);
        char *b = strtok_r(NULL, "|", &save);
        if (r && g && b) {
            lumi_rgb_set_solid((uint8_t)atoi(r), (uint8_t)atoi(g), (uint8_t)atoi(b));
        }
    }
}

static void handle_line(char *line) {
    char *save = NULL;
    char *root = strtok_r(line, "|", &save);
    if (!root) return;

    if (strcmp(root, "HELLO") == 0) {
        write_text("LUMIPAD|1\r\n");
    } else if (strcmp(root, "NP") == 0) {
        handle_np(save);
    } else if (strcmp(root, "RGB") == 0) {
        handle_rgb(save);
    }
}

static void lumi_app_thread(void) {
    while (!device_is_ready(app_uart)) {
        k_sleep(K_MSEC(100));
    }

    char line[LINE_MAX];
    size_t len = 0;

    for (;;) {
        unsigned char c;
        bool read_any = false;

        while (uart_poll_in(app_uart, &c) == 0) {
            read_any = true;

            if (c == '\n') {
                line[len] = '\0';
                if (len && line[len - 1] == '\r') {
                    line[len - 1] = '\0';
                }
                handle_line(line);
                len = 0;
                continue;
            }

            if (len < sizeof(line) - 1) {
                line[len++] = (char)c;
            } else {
                len = 0;
            }
        }

        if (!read_any) {
            k_sleep(K_MSEC(5));
        }
    }
}

K_THREAD_DEFINE(lumi_app_thread_id, 1536, lumi_app_thread,
                NULL, NULL, NULL, K_LOWEST_APPLICATION_THREAD_PRIO, 0, 500);
