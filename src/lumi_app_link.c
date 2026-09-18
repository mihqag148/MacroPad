/* SPDX-License-Identifier: MIT */

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>

#include <zephyr/bluetooth/gatt.h>
#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/uart.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>

#include "lumi_now_playing.h"
#include "lumi_rgb.h"

LOG_MODULE_REGISTER(lumi_app, CONFIG_ZMK_LOG_LEVEL);

#define APP_UART_NODE DT_NODELABEL(lumi_app_uart)
#define LINE_MAX 3200
#define BITMAP_TMP_MAX LUMI_TITLE_BITMAP_BYTES

#define LUMI_SERVICE_UUID     BT_UUID_128_ENCODE(0xD8A90001, 0x6B5A, 0x4C3B, 0x9F2A, 0x7C4E4C554D49)
#define LUMI_CHAR_UUID     BT_UUID_128_ENCODE(0xD8A90002, 0x6B5A, 0x4C3B, 0x9F2A, 0x7C4E4C554D49)

static const struct device *const app_uart = DEVICE_DT_GET(APP_UART_NODE);

static char usb_line[LINE_MAX];
static size_t usb_len;
static char ble_line[LINE_MAX];
static size_t ble_len;

static uint8_t bitmap_tmp[BITMAP_TMP_MAX];
K_MUTEX_DEFINE(bitmap_lock);

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

static void write_text_usb(const char *s) {
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

static void handle_txt(char *save) {
    char *kind = strtok_r(NULL, "|", &save);
    char *width_s = strtok_r(NULL, "|", &save);
    char *height_s = strtok_r(NULL, "|", &save);
    char *hex = strtok_r(NULL, "|", &save);

    if (!kind || !width_s || !height_s || !hex) {
        return;
    }

    int width = atoi(width_s);
    int height = atoi(height_s);
    bool is_title = kind[0] == 'T';

    if (width != LUMI_TEXT_W ||
        height != (is_title ? LUMI_TITLE_H : LUMI_ARTIST_H)) {
        return;
    }

    size_t expected = is_title ? LUMI_TITLE_BITMAP_BYTES : LUMI_ARTIST_BITMAP_BYTES;
    size_t hex_len = strlen(hex);
    if (hex_len < expected * 2U || expected > sizeof(bitmap_tmp)) {
        return;
    }

    k_mutex_lock(&bitmap_lock, K_FOREVER);

    for (size_t i = 0; i < expected; i++) {
        int hi = hex_nibble(hex[i * 2U]);
        int lo = hex_nibble(hex[i * 2U + 1U]);
        if (hi < 0 || lo < 0) {
            k_mutex_unlock(&bitmap_lock);
            return;
        }
        bitmap_tmp[i] = (uint8_t)((hi << 4) | lo);
    }

    lumi_now_playing_set_bitmap(is_title, bitmap_tmp, expected);
    k_mutex_unlock(&bitmap_lock);
}

static void handle_line(char *line, bool from_usb) {
    char *save = NULL;
    char *root = strtok_r(line, "|", &save);
    if (!root) return;

    if (strcmp(root, "HELLO") == 0) {
        if (from_usb) {
            write_text_usb("LUMIPAD|2\r\n");
        }
    } else if (strcmp(root, "NP") == 0) {
        handle_np(save);
    } else if (strcmp(root, "RGB") == 0) {
        handle_rgb(save);
    } else if (strcmp(root, "TXT") == 0) {
        handle_txt(save);
    }
}

static void feed_bytes(char *line, size_t *line_len,
                       const uint8_t *data, size_t len,
                       bool from_usb) {
    for (size_t i = 0; i < len; i++) {
        uint8_t c = data[i];

        if (c == '\n') {
            line[*line_len] = '\0';
            if (*line_len && line[*line_len - 1] == '\r') {
                line[*line_len - 1] = '\0';
            }

            handle_line(line, from_usb);
            *line_len = 0;
            continue;
        }

        if (*line_len < LINE_MAX - 1) {
            line[(*line_len)++] = (char)c;
        } else {
            *line_len = 0;
        }
    }
}

/* ---------------- Bluetooth GATT transport ---------------- */

static const char lumi_identity[] = "LUMIPAD|2";

static ssize_t read_lumi(struct bt_conn *conn, const struct bt_gatt_attr *attr,
                         void *buf, uint16_t len, uint16_t offset) {
    ARG_UNUSED(attr);
    return bt_gatt_attr_read(conn, attr, buf, len, offset,
                             lumi_identity, strlen(lumi_identity));
}

static ssize_t write_lumi(struct bt_conn *conn, const struct bt_gatt_attr *attr,
                          const void *buf, uint16_t len, uint16_t offset,
                          uint8_t flags) {
    ARG_UNUSED(conn);
    ARG_UNUSED(attr);
    ARG_UNUSED(flags);

    if (offset != 0) {
        return BT_GATT_ERR(BT_ATT_ERR_INVALID_OFFSET);
    }

    feed_bytes(ble_line, &ble_len, (const uint8_t *)buf, len, false);
    return len;
}

BT_GATT_SERVICE_DEFINE(
    lumi_app_svc,
    BT_GATT_PRIMARY_SERVICE(BT_UUID_DECLARE_128(LUMI_SERVICE_UUID)),
    BT_GATT_CHARACTERISTIC(
        BT_UUID_DECLARE_128(LUMI_CHAR_UUID),
        BT_GATT_CHRC_READ | BT_GATT_CHRC_WRITE | BT_GATT_CHRC_WRITE_WITHOUT_RESP,
        BT_GATT_PERM_READ | BT_GATT_PERM_WRITE,
        read_lumi, write_lumi, NULL));

/* ---------------- USB fallback transport ---------------- */

static void lumi_app_thread(void) {
    while (!device_is_ready(app_uart)) {
        k_sleep(K_MSEC(100));
    }

    for (;;) {
        unsigned char c;
        bool read_any = false;

        while (uart_poll_in(app_uart, &c) == 0) {
            read_any = true;
            feed_bytes(usb_line, &usb_len, &c, 1, true);
        }

        if (!read_any) {
            k_sleep(K_MSEC(5));
        }
    }
}

K_THREAD_DEFINE(lumi_app_thread_id, 1536, lumi_app_thread,
                NULL, NULL, NULL, K_LOWEST_APPLICATION_THREAD_PRIO, 0, 500);
