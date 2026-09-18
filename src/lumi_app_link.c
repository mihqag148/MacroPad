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
#include <zephyr/sys/base64.h>
#include <zephyr/sys/reboot.h>

#include "lumi_now_playing.h"
#include "lumi_rgb.h"
#include "lumi_ui_config.h"

LOG_MODULE_REGISTER(lumi_app, CONFIG_ZMK_LOG_LEVEL);

#define APP_UART_NODE DT_NODELABEL(lumi_app_uart)
#define LINE_MAX 1200
#define BITMAP_TMP_MAX LUMI_TITLE_BITMAP_MAX_BYTES

#define LUMI_SERVICE_UUID     BT_UUID_128_ENCODE(0xD8A90001, 0x6B5A, 0x4C3B, 0x9F2A, 0x7C4E4C554D49)
#define LUMI_CHAR_UUID     BT_UUID_128_ENCODE(0xD8A90002, 0x6B5A, 0x4C3B, 0x9F2A, 0x7C4E4C554D49)

static const struct device *const app_uart = DEVICE_DT_GET(APP_UART_NODE);

static char usb_line[LINE_MAX];
static size_t usb_len;
static char ble_line[LINE_MAX];
static size_t ble_len;

static uint8_t bitmap_tmp[BITMAP_TMP_MAX];
static uint8_t artwork_tmp[LUMI_ARTWORK_BYTES];
static uint8_t saver_chunk_tmp[256];
static char lumi_status[64] = "LUMIPAD|2|SAVER:EMPTY";
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
    char *source = strtok_r(NULL, "|", &save);
    char *title = strtok_r(NULL, "|", &save);
    char *artist = strtok_r(NULL, "|", &save);

    if (!pos_s || !dur_s || !playing_s || !source || !title || !artist) {
        return;
    }

    url_decode(source);
    url_decode(title);
    url_decode(artist);

    lumi_now_playing_update(
        source,
        title,
        artist,
        (uint32_t)strtoul(pos_s, NULL, 10),
        (uint32_t)strtoul(dur_s, NULL, 10),
        atoi(playing_s) != 0
    );
}

static void handle_rgb(char *save) {
    lumi_ui_note_activity();
    char *cmd = strtok_r(NULL, "|", &save);
    if (!cmd) return;

    if (strcmp(cmd, "EN") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_enabled(atoi(v) != 0);
    } else if (strcmp(cmd, "BRI") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_brightness_percent((uint8_t)atoi(v));
    } else if (strcmp(cmd, "SPD") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_speed_percent((uint8_t)atoi(v));
    } else if (strcmp(cmd, "AUTO") == 0) {
        lumi_rgb_set_auto(true);
    } else if (strcmp(cmd, "FX") == 0) {
        char *v = strtok_r(NULL, "|", &save);
        if (v) lumi_rgb_set_effect((uint8_t)atoi(v));
    } else if (strcmp(cmd, "STATE") == 0) {
        char *enabled = strtok_r(NULL, "|", &save);
        char *brightness = strtok_r(NULL, "|", &save);
        char *speed = strtok_r(NULL, "|", &save);
        char *auto_s = strtok_r(NULL, "|", &save);
        char *effect = strtok_r(NULL, "|", &save);
        char *r = strtok_r(NULL, "|", &save);
        char *g = strtok_r(NULL, "|", &save);
        char *b = strtok_r(NULL, "|", &save);

        if (enabled && brightness && speed && auto_s && effect && r && g && b) {
            lumi_rgb_set_brightness_percent((uint8_t)atoi(brightness));
            lumi_rgb_set_speed_percent((uint8_t)atoi(speed));

            if (atoi(auto_s) != 0) {
                lumi_rgb_set_auto(true);
            } else if ((uint8_t)atoi(effect) == LUMI_RGB_EFFECT_SOLID) {
                lumi_rgb_set_solid(
                    (uint8_t)atoi(r),
                    (uint8_t)atoi(g),
                    (uint8_t)atoi(b));
            } else {
                lumi_rgb_set_effect((uint8_t)atoi(effect));
            }

            lumi_rgb_set_enabled(atoi(enabled) != 0);
        }
    } else if (strcmp(cmd, "SOLID") == 0) {
        char *r = strtok_r(NULL, "|", &save);
        char *g = strtok_r(NULL, "|", &save);
        char *b = strtok_r(NULL, "|", &save);
        if (r && g && b) {
            lumi_rgb_set_solid((uint8_t)atoi(r), (uint8_t)atoi(g), (uint8_t)atoi(b));
        }
    }
}

static void handle_art(char *save) {
    char *base64 = strtok_r(NULL, "|", &save);
    if (!base64) {
        return;
    }

    size_t decoded_len = 0;
    int rc = base64_decode(
        artwork_tmp,
        sizeof(artwork_tmp),
        &decoded_len,
        (const uint8_t *)base64,
        strlen(base64));

    if (rc != 0 || decoded_len != LUMI_ARTWORK_BYTES) {
        return;
    }

    lumi_now_playing_set_artwork(artwork_tmp, decoded_len);
}

static void handle_cfg(char *save) {
    lumi_ui_note_activity();
    char *cmd = strtok_r(NULL, "|", &save);
    if (!cmd) {
        return;
    }

    if (strcmp(cmd, "WALL") == 0) {
        char *r1 = strtok_r(NULL, "|", &save);
        char *g1 = strtok_r(NULL, "|", &save);
        char *b1 = strtok_r(NULL, "|", &save);
        char *r2 = strtok_r(NULL, "|", &save);
        char *g2 = strtok_r(NULL, "|", &save);
        char *b2 = strtok_r(NULL, "|", &save);

        if (r1 && g1 && b1 && r2 && g2 && b2) {
            lumi_ui_set_wallpaper(
                (uint8_t)atoi(r1), (uint8_t)atoi(g1), (uint8_t)atoi(b1),
                (uint8_t)atoi(r2), (uint8_t)atoi(g2), (uint8_t)atoi(b2));
        }
    } else if (strcmp(cmd, "SAVER") == 0) {
        char *enabled = strtok_r(NULL, "|", &save);
        char *style = strtok_r(NULL, "|", &save);
        char *delay = strtok_r(NULL, "|", &save);
        char *r1 = strtok_r(NULL, "|", &save);
        char *g1 = strtok_r(NULL, "|", &save);
        char *b1 = strtok_r(NULL, "|", &save);
        char *r2 = strtok_r(NULL, "|", &save);
        char *g2 = strtok_r(NULL, "|", &save);
        char *b2 = strtok_r(NULL, "|", &save);

        if (enabled && style && delay && r1 && g1 && b1 && r2 && g2 && b2) {
            lumi_ui_set_screensaver(
                atoi(enabled) != 0,
                (uint8_t)atoi(style),
                (uint32_t)strtoul(delay, NULL, 10),
                (uint8_t)atoi(r1), (uint8_t)atoi(g1), (uint8_t)atoi(b1),
                (uint8_t)atoi(r2), (uint8_t)atoi(g2), (uint8_t)atoi(b2));
        }
    } else if (strcmp(cmd, "SAVERDELAY") == 0) {
        char *seconds = strtok_r(NULL, "|", &save);
        if (seconds) {
            lumi_ui_set_screensaver_delay((uint32_t)strtoul(seconds, NULL, 10));
        }
    } else if (strcmp(cmd, "SLEEP") == 0) {
        char *seconds = strtok_r(NULL, "|", &save);
        if (seconds) {
            lumi_ui_set_sleep_timeout((uint32_t)strtoul(seconds, NULL, 10));
        }
    }
}

static void handle_sys(char *save) {
    char *cmd = strtok_r(NULL, "|", &save);
    if (!cmd) {
        return;
    }

    if (strcmp(cmd, "RESTART") == 0) {
        k_sleep(K_MSEC(80));
        sys_reboot(SYS_REBOOT_WARM);
    } else if (strcmp(cmd, "DFU") == 0) {
        /* nice!nano v2 uses the Adafruit nRF52 bootloader magic reset value. */
        k_sleep(K_MSEC(80));
        sys_reboot(0x57);
    }
}

static void handle_savbegin(char *save) {
    char *count_s = strtok_r(NULL, "|", &save);
    char *interval_s = strtok_r(NULL, "|", &save);

    if (!count_s || !interval_s) {
        return;
    }

    uint8_t count = (uint8_t)atoi(count_s);
    lumi_ui_saver_anim_begin(
        count,
        (uint16_t)atoi(interval_s));
    snprintf(lumi_status, sizeof(lumi_status),
             "LUMIPAD|2|SAVER:UPLOADING:0/%u", (unsigned int)count);
}

static void handle_savchunk(char *save) {
    char *index_s = strtok_r(NULL, "|", &save);
    char *offset_s = strtok_r(NULL, "|", &save);
    char *base64 = strtok_r(NULL, "|", &save);

    if (!index_s || !offset_s || !base64) {
        snprintf(lumi_status, sizeof(lumi_status), "LUMIPAD|2|SAVER:ERROR");
        return;
    }

    size_t decoded_len = 0;
    int rc = base64_decode(
        saver_chunk_tmp,
        sizeof(saver_chunk_tmp),
        &decoded_len,
        (const uint8_t *)base64,
        strlen(base64));

    if (rc != 0 || decoded_len == 0U) {
        snprintf(lumi_status, sizeof(lumi_status), "LUMIPAD|2|SAVER:ERROR");
        return;
    }

    uint8_t index = (uint8_t)atoi(index_s);
    uint16_t offset = (uint16_t)atoi(offset_s);

    lumi_ui_saver_anim_chunk(index, offset, saver_chunk_tmp, decoded_len);

    if ((size_t)offset + decoded_len >= LUMI_SAVER_FRAME_BYTES) {
        snprintf(lumi_status, sizeof(lumi_status),
                 "LUMIPAD|2|SAVER:UPLOADING:%u",
                 (unsigned int)(index + 1U));
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

    int max_width = is_title
        ? LUMI_TITLE_BITMAP_MAX_W
        : LUMI_ARTIST_BITMAP_MAX_W;

    if (width < LUMI_TEXT_W ||
        width > max_width ||
        height != (is_title ? LUMI_TITLE_H : LUMI_ARTIST_H)) {
        return;
    }

    size_t expected = (((size_t)width * height) + 7U) / 8U;
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

    lumi_now_playing_set_bitmap(is_title, (uint16_t)width, bitmap_tmp, expected);
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
    } else if (strcmp(root, "CFG") == 0) {
        handle_cfg(save);
    } else if (strcmp(root, "SYS") == 0) {
        handle_sys(save);
    } else if (strcmp(root, "TXT") == 0) {
        handle_txt(save);
    } else if (strcmp(root, "ART") == 0) {
        handle_art(save);
    } else if (strcmp(root, "SAVBEGIN") == 0) {
        handle_savbegin(save);
    } else if (strcmp(root, "SAVCHUNK") == 0) {
        handle_savchunk(save);
    } else if (strcmp(root, "SAVEND") == 0) {
        lumi_ui_saver_anim_end();
        snprintf(lumi_status, sizeof(lumi_status),
                 lumi_ui_saver_anim_is_valid()
                     ? "LUMIPAD|2|SAVER:READY"
                     : "LUMIPAD|2|SAVER:ERROR");
    } else if (strcmp(root, "SAVCLEAR") == 0) {
        lumi_ui_saver_anim_clear();
        snprintf(lumi_status, sizeof(lumi_status), "LUMIPAD|2|SAVER:EMPTY");
    } else if (strcmp(root, "CLEAR") == 0) {
        lumi_now_playing_clear();
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

static ssize_t read_lumi(struct bt_conn *conn, const struct bt_gatt_attr *attr,
                         void *buf, uint16_t len, uint16_t offset) {
    ARG_UNUSED(attr);
    return bt_gatt_attr_read(conn, attr, buf, len, offset,
                             lumi_status, strlen(lumi_status));
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
