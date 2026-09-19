/* SPDX-License-Identifier: MIT */

#include <ctype.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>

#include <zephyr/bluetooth/gatt.h>
#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/drivers/uart.h>
#include <zephyr/kernel.h>
#include <zephyr/linker/linker-defs.h>
#include <zephyr/logging/log.h>
#include <zephyr/sys/base64.h>
#include <zephyr/sys/reboot.h>

#include "lumi_now_playing.h"
#include "lumi_rgb.h"
#include "lumi_ui_config.h"
#include "lumi_diag.h"

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

static char text_upload_kind;
static uint16_t text_upload_width;
static uint16_t text_upload_height;
static size_t text_upload_total;
static size_t text_upload_received;

static size_t artwork_upload_total;
static size_t artwork_upload_received;
static char lumi_status[96] = "LUMIPAD|2|SAVER:EMPTY";
K_MUTEX_DEFINE(bitmap_lock);

#define DIAG_CAPACITY 16
#define DIAG_TEXT_MAX 72

struct lumi_diag_entry {
    uint32_t seq;
    char level;
    char text[DIAG_TEXT_MAX];
};

static void write_text_usb(const char *s);

static struct lumi_diag_entry diag_entries[DIAG_CAPACITY];
static uint8_t diag_head;
static uint8_t diag_count;
static uint32_t diag_seq;
K_MUTEX_DEFINE(diag_lock);

void lumi_diag_report(char level, const char *fmt, ...) {
    k_mutex_lock(&diag_lock, K_FOREVER);

    struct lumi_diag_entry *entry = &diag_entries[diag_head];
    entry->seq = ++diag_seq;
    entry->level = level;

    va_list args;
    va_start(args, fmt);
    vsnprintf(entry->text, sizeof(entry->text), fmt, args);
    va_end(args);

    diag_head = (uint8_t)((diag_head + 1U) % DIAG_CAPACITY);
    if (diag_count < DIAG_CAPACITY) {
        diag_count++;
    }

    k_mutex_unlock(&diag_lock);
}

static bool diag_next_after(uint32_t after, struct lumi_diag_entry *out) {
    bool found = false;

    k_mutex_lock(&diag_lock, K_FOREVER);
    uint8_t start = (uint8_t)((diag_head + DIAG_CAPACITY - diag_count) % DIAG_CAPACITY);

    for (uint8_t i = 0U; i < diag_count; i++) {
        struct lumi_diag_entry *entry =
            &diag_entries[(uint8_t)((start + i) % DIAG_CAPACITY)];
        if (entry->seq > after) {
            *out = *entry;
            found = true;
            break;
        }
    }

    k_mutex_unlock(&diag_lock);
    return found;
}

static void handle_diag_log(char *save, bool from_usb) {
    char *after_s = strtok_r(NULL, "|", &save);
    uint32_t after = after_s ? (uint32_t)strtoul(after_s, NULL, 10) : 0U;
    struct lumi_diag_entry entry = {0};
    char response[128];

    if (diag_next_after(after, &entry)) {
        snprintf(response, sizeof(response), "LOG|%u|%c|%s",
                 (unsigned int)entry.seq, entry.level, entry.text);
    } else {
        snprintf(response, sizeof(response), "LOG|NONE|%u",
                 (unsigned int)diag_seq);
    }

    if (from_usb) {
        write_text_usb(response);
        write_text_usb("\r\n");
    } else {
        snprintf(lumi_status, sizeof(lumi_status), "%s", response);
    }
}

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
        lumi_diag_report('E', "NP invalid payload");
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
    } else if (strcmp(cmd, "PROFILE") == 0) {
        char *index = strtok_r(NULL, "|", &save);
        char *effect = strtok_r(NULL, "|", &save);
        char *r = strtok_r(NULL, "|", &save);
        char *g = strtok_r(NULL, "|", &save);
        char *b = strtok_r(NULL, "|", &save);

        if (index && effect && r && g && b) {
            lumi_rgb_set_profile(
                (uint8_t)atoi(index),
                (uint8_t)atoi(effect),
                (uint8_t)atoi(r),
                (uint8_t)atoi(g),
                (uint8_t)atoi(b));
        }
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
        lumi_diag_report('E', "ART decode rc=%d len=%u", rc, (unsigned int)decoded_len);
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
    } else if (strcmp(cmd, "SAVERNOW") == 0) {
        lumi_diag_report('I', "Screensaver show now");
        lumi_ui_show_screensaver_now();
    } else if (strcmp(cmd, "SLEEP") == 0) {
        char *seconds = strtok_r(NULL, "|", &save);
        if (seconds) {
            uint32_t value = (uint32_t)strtoul(seconds, NULL, 10);
            lumi_diag_report('I', "Sleep timeout=%us", (unsigned int)value);
            lumi_ui_set_sleep_timeout(value);
        }
    }
}

static void handle_sys(char *save) {
    char *cmd = strtok_r(NULL, "|", &save);
    if (!cmd) {
        return;
    }

    if (strcmp(cmd, "RESTART") == 0) {
        lumi_diag_report('I', "System restart requested");
        k_sleep(K_MSEC(80));
        sys_reboot(SYS_REBOOT_WARM);
    } else if (strcmp(cmd, "DFU") == 0) {
        lumi_diag_report('I', "DFU requested");
        /* nice!nano v2 uses the Adafruit nRF52 bootloader magic reset value. */
        k_sleep(K_MSEC(80));
        sys_reboot(0x57);
    }
}

static void handle_savbegin(char *save) {
    char *count_s = strtok_r(NULL, "|", &save);
    char *interval_s = strtok_r(NULL, "|", &save);
    char *timing_s = strtok_r(NULL, "|", &save);

    if (!count_s || !interval_s) {
        return;
    }

    uint8_t count = (uint8_t)atoi(count_s);
    uint16_t fallback_interval = (uint16_t)atoi(interval_s);
    bool ok = lumi_ui_saver_anim_begin(count, fallback_interval);

    if (ok && timing_s && timing_s[0] != '\0') {
        char *timing_save = NULL;
        char *token = strtok_r(timing_s, ",", &timing_save);
        uint8_t index = 0U;

        while (token && index < count) {
            (void)lumi_ui_saver_anim_set_frame_interval(
                index,
                (uint16_t)atoi(token));
            index++;
            token = strtok_r(NULL, ",", &timing_save);
        }

        if (index != count) {
            lumi_diag_report(
                'W',
                "SAVBEGIN timing count=%u expected=%u",
                (unsigned int)index,
                (unsigned int)count);
        }
    }

    snprintf(lumi_status, sizeof(lumi_status),
             ok
                 ? "LUMIPAD|2|SAVER:UPLOADING:0/%u"
                 : "LUMIPAD|2|SAVER:ERROR",
             (unsigned int)count);
    lumi_diag_report(ok ? 'I' : 'E', "SAVBEGIN frames=%u interval=%u %s",
              (unsigned int)count, (unsigned int)fallback_interval,
              ok ? "OK" : "ERROR");
}

static void handle_savchunk(char *save) {
    char *index_s = strtok_r(NULL, "|", &save);
    char *offset_s = strtok_r(NULL, "|", &save);
    char *base64 = strtok_r(NULL, "|", &save);

    if (!index_s || !offset_s || !base64) {
        snprintf(lumi_status, sizeof(lumi_status), "LUMIPAD|2|SAVER:ERROR");
        lumi_diag_report('E', "SAVCHUNK missing field");
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
        lumi_diag_report('E', "SAVCHUNK base64 rc=%d len=%u", rc, (unsigned int)decoded_len);
        return;
    }

    uint8_t index = (uint8_t)atoi(index_s);
    uint16_t offset = (uint16_t)atoi(offset_s);

    if (!lumi_ui_saver_anim_chunk(
            index, offset, saver_chunk_tmp, decoded_len)) {
        snprintf(lumi_status, sizeof(lumi_status), "LUMIPAD|2|SAVER:ERROR");
        lumi_diag_report('E', "SAVCHUNK write frame=%u off=%u len=%u",
                  (unsigned int)index, (unsigned int)offset,
                  (unsigned int)decoded_len);
        return;
    }

    if ((size_t)offset + decoded_len >= LUMI_SAVER_FRAME_BYTES) {
        snprintf(lumi_status, sizeof(lumi_status),
                 "LUMIPAD|2|SAVER:UPLOADING:%u",
                 (unsigned int)(index + 1U));
        lumi_diag_report('I', "Saver frame %u complete", (unsigned int)(index + 1U));
    }
}


static void handle_txtbegin(char *save) {
    char *kind_s = strtok_r(NULL, "|", &save);
    char *width_s = strtok_r(NULL, "|", &save);
    char *height_s = strtok_r(NULL, "|", &save);
    char *total_s = strtok_r(NULL, "|", &save);

    if (!kind_s || !width_s || !height_s || !total_s) {
        lumi_diag_report('E', "TXTBEGIN missing field");
        return;
    }

    bool is_title = kind_s[0] == 'T';
    int width = atoi(width_s);
    int height = atoi(height_s);
    size_t total = (size_t)strtoul(total_s, NULL, 10);
    int max_width = is_title
        ? LUMI_TITLE_BITMAP_MAX_W
        : LUMI_ARTIST_BITMAP_MAX_W;
    int expected_height = is_title ? LUMI_TITLE_H : LUMI_ARTIST_H;
    size_t expected =
        (((size_t)width * (size_t)expected_height) + 7U) / 8U;

    if ((kind_s[0] != 'T' && kind_s[0] != 'A') ||
        width < LUMI_TEXT_W ||
        width > max_width ||
        height != expected_height ||
        total != expected ||
        total > sizeof(bitmap_tmp)) {
        lumi_diag_report('E', "TXTBEGIN invalid kind=%c w=%d h=%d total=%u",
                         kind_s[0], width, height, (unsigned int)total);
        text_upload_kind = 0;
        text_upload_received = 0U;
        return;
    }

    k_mutex_lock(&bitmap_lock, K_FOREVER);
    memset(bitmap_tmp, 0, sizeof(bitmap_tmp));
    text_upload_kind = kind_s[0];
    text_upload_width = (uint16_t)width;
    text_upload_height = (uint16_t)height;
    text_upload_total = total;
    text_upload_received = 0U;
    k_mutex_unlock(&bitmap_lock);
}

static void handle_txtchunk(char *save) {
    char *kind_s = strtok_r(NULL, "|", &save);
    char *offset_s = strtok_r(NULL, "|", &save);
    char *hex = strtok_r(NULL, "|", &save);

    if (!kind_s || !offset_s || !hex || text_upload_kind == 0) {
        lumi_diag_report('E', "TXTCHUNK invalid state");
        return;
    }

    size_t offset = (size_t)strtoul(offset_s, NULL, 10);
    size_t hex_len = strlen(hex);
    size_t bytes = hex_len / 2U;

    if (kind_s[0] != text_upload_kind ||
        (hex_len & 1U) != 0U ||
        offset != text_upload_received ||
        offset + bytes > text_upload_total) {
        lumi_diag_report('E', "TXTCHUNK mismatch kind=%c off=%u len=%u",
                         kind_s[0], (unsigned int)offset,
                         (unsigned int)bytes);
        return;
    }

    k_mutex_lock(&bitmap_lock, K_FOREVER);
    for (size_t i = 0; i < bytes; i++) {
        int hi = hex_nibble(hex[i * 2U]);
        int lo = hex_nibble(hex[i * 2U + 1U]);
        if (hi < 0 || lo < 0) {
            k_mutex_unlock(&bitmap_lock);
            lumi_diag_report('E', "TXTCHUNK bad hex off=%u",
                             (unsigned int)offset);
            return;
        }
        bitmap_tmp[offset + i] = (uint8_t)((hi << 4) | lo);
    }
    text_upload_received += bytes;
    k_mutex_unlock(&bitmap_lock);
}

static void handle_txtend(char *save) {
    char *kind_s = strtok_r(NULL, "|", &save);

    if (!kind_s ||
        text_upload_kind == 0 ||
        kind_s[0] != text_upload_kind ||
        text_upload_received != text_upload_total) {
        lumi_diag_report('E', "TXTEND incomplete kind=%c got=%u need=%u",
                         kind_s ? kind_s[0] : '?',
                         (unsigned int)text_upload_received,
                         (unsigned int)text_upload_total);
        text_upload_kind = 0;
        return;
    }

    bool is_title = text_upload_kind == 'T';

    k_mutex_lock(&bitmap_lock, K_FOREVER);
    lumi_now_playing_set_bitmap(
        is_title,
        text_upload_width,
        bitmap_tmp,
        text_upload_total);
    k_mutex_unlock(&bitmap_lock);

    lumi_diag_report('I', "Text bitmap %c ready w=%u bytes=%u",
                     text_upload_kind,
                     (unsigned int)text_upload_width,
                     (unsigned int)text_upload_total);

    text_upload_kind = 0;
    text_upload_received = 0U;
    text_upload_total = 0U;
}

static void handle_artbegin(char *save) {
    char *total_s = strtok_r(NULL, "|", &save);
    size_t total = total_s ? (size_t)strtoul(total_s, NULL, 10) : 0U;

    if (total != LUMI_ARTWORK_BYTES) {
        lumi_diag_report('E', "ARTBEGIN invalid total=%u",
                         (unsigned int)total);
        artwork_upload_total = 0U;
        artwork_upload_received = 0U;
        return;
    }

    memset(artwork_tmp, 0, sizeof(artwork_tmp));
    artwork_upload_total = total;
    artwork_upload_received = 0U;
}

static void handle_artchunk(char *save) {
    char *offset_s = strtok_r(NULL, "|", &save);
    char *base64 = strtok_r(NULL, "|", &save);

    if (!offset_s || !base64 ||
        artwork_upload_total != LUMI_ARTWORK_BYTES) {
        lumi_diag_report('E', "ARTCHUNK invalid state");
        return;
    }

    size_t offset = (size_t)strtoul(offset_s, NULL, 10);
    uint8_t chunk[192];
    size_t decoded_len = 0U;
    int rc = base64_decode(
        chunk, sizeof(chunk), &decoded_len,
        (const uint8_t *)base64, strlen(base64));

    if (rc != 0 ||
        offset != artwork_upload_received ||
        offset + decoded_len > artwork_upload_total) {
        lumi_diag_report('E', "ARTCHUNK rc=%d off=%u got=%u",
                         rc, (unsigned int)offset,
                         (unsigned int)decoded_len);
        return;
    }

    memcpy(&artwork_tmp[offset], chunk, decoded_len);
    artwork_upload_received += decoded_len;
}

static void handle_artend(void) {
    if (artwork_upload_total != LUMI_ARTWORK_BYTES ||
        artwork_upload_received != artwork_upload_total) {
        lumi_diag_report('E', "ARTEND incomplete got=%u need=%u",
                         (unsigned int)artwork_upload_received,
                         (unsigned int)artwork_upload_total);
        artwork_upload_total = 0U;
        artwork_upload_received = 0U;
        return;
    }

    lumi_now_playing_set_artwork(artwork_tmp, artwork_upload_total);
    lumi_diag_report('I', "Artwork ready bytes=%u",
                     (unsigned int)artwork_upload_total);

    artwork_upload_total = 0U;
    artwork_upload_received = 0U;
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

static void handle_mem(bool from_usb) {
    size_t flash_used = (size_t)_flash_used;
    size_t flash_total = DT_REG_SIZE(DT_NODELABEL(code_partition));
    size_t ram_used = (size_t)(_image_ram_end - _image_ram_start);
    size_t ram_total = DT_REG_SIZE(DT_NODELABEL(sram0));

    char response[96];
    snprintf(
        response,
        sizeof(response),
        "MEM|%u|%u|%u|%u",
        (unsigned int)flash_used,
        (unsigned int)flash_total,
        (unsigned int)ram_used,
        (unsigned int)ram_total);

    if (from_usb) {
        write_text_usb(response);
        write_text_usb("\r\n");
    } else {
        snprintf(lumi_status, sizeof(lumi_status), "%s", response);
    }
}

static void handle_line(char *line, bool from_usb) {
    char *save = NULL;
    char *root = strtok_r(line, "|", &save);
    if (!root) return;

    if (strcmp(root, "HELLO") == 0) {
        if (from_usb) {
            write_text_usb("LUMIPAD|2\r\n");
        }
    } else if (strcmp(root, "MEM") == 0) {
        handle_mem(from_usb);
    } else if (strcmp(root, "LOG") == 0) {
        handle_diag_log(save, from_usb);
    } else if (strcmp(root, "NP") == 0) {
        handle_np(save);
    } else if (strcmp(root, "RGB") == 0) {
        handle_rgb(save);
    } else if (strcmp(root, "CFG") == 0) {
        handle_cfg(save);
    } else if (strcmp(root, "SYS") == 0) {
        handle_sys(save);
    } else if (strcmp(root, "TXTBEGIN") == 0) {
        handle_txtbegin(save);
    } else if (strcmp(root, "TXTCHUNK") == 0) {
        handle_txtchunk(save);
    } else if (strcmp(root, "TXTEND") == 0) {
        handle_txtend(save);
    } else if (strcmp(root, "TXT") == 0) {
        /* Legacy single-line format kept for older apps. */
        handle_txt(save);
    } else if (strcmp(root, "ARTBEGIN") == 0) {
        handle_artbegin(save);
    } else if (strcmp(root, "ARTCHUNK") == 0) {
        handle_artchunk(save);
    } else if (strcmp(root, "ARTEND") == 0) {
        handle_artend();
    } else if (strcmp(root, "ART") == 0) {
        /* Legacy single-line format kept for older apps. */
        handle_art(save);
    } else if (strcmp(root, "SAVBEGIN") == 0) {
        handle_savbegin(save);
        if (from_usb) {
            write_text_usb(
                strstr(lumi_status, "SAVER:ERROR") != NULL
                    ? "SAVACK|ERROR\r\n"
                    : "SAVACK|BEGIN\r\n");
        }
    } else if (strcmp(root, "SAVCHUNK") == 0) {
        handle_savchunk(save);
        if (from_usb) {
            write_text_usb(
                strstr(lumi_status, "SAVER:ERROR") != NULL
                    ? "SAVACK|ERROR\r\n"
                    : "SAVACK|CHUNK\r\n");
        }
    } else if (strcmp(root, "SAVEND") == 0) {
        bool saver_ok = lumi_ui_saver_anim_end() &&
                        lumi_ui_saver_anim_is_valid();
        snprintf(lumi_status, sizeof(lumi_status),
                 saver_ok
                     ? "LUMIPAD|2|SAVER:READY"
                     : "LUMIPAD|2|SAVER:ERROR");
        lumi_diag_report(saver_ok ? 'I' : 'E',
                  saver_ok ? "SAVEND READY" : "SAVEND ERROR");
        if (from_usb) {
            write_text_usb(
                saver_ok
                    ? "SAVACK|READY\r\n"
                    : "SAVACK|ERROR\r\n");
        }
    } else if (strcmp(root, "SAVCLEAR") == 0) {
        lumi_ui_saver_anim_clear();
        snprintf(lumi_status, sizeof(lumi_status), "LUMIPAD|2|SAVER:EMPTY");
    } else if (strcmp(root, "CLEAR") == 0) {
        lumi_now_playing_clear();
    } else {
        lumi_diag_report('W', "Unknown command: %.20s", root);
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
            lumi_diag_report('E', "RX line overflow");
            *line_len = 0;
        }
    }
}

/* ---------------- Bluetooth GATT transport ---------------- */

static ssize_t read_lumi(struct bt_conn *conn, const struct bt_gatt_attr *attr,
                         void *buf, uint16_t len, uint16_t offset) {
    ARG_UNUSED(attr);

    /* On normal reads report the actual persisted saver state. Keep explicit
     * upload/error text only while an upload is in progress or has failed.
     */
    const char *status = lumi_status;
    if (strncmp(lumi_status, "MEM|", 4) != 0 &&
        strncmp(lumi_status, "LOG|", 4) != 0 &&
        strstr(lumi_status, "SAVER:UPLOADING") == NULL &&
        strstr(lumi_status, "SAVER:ERROR") == NULL) {
        status = lumi_ui_saver_anim_is_valid()
            ? "LUMIPAD|2|SAVER:READY"
            : "LUMIPAD|2|SAVER:EMPTY";
    }

    ssize_t rc = bt_gatt_attr_read(conn, attr, buf, len, offset,
                                    status, strlen(status));

    if (strncmp(lumi_status, "MEM|", 4) == 0) {
        snprintf(
            lumi_status,
            sizeof(lumi_status),
            lumi_ui_saver_anim_is_valid()
                ? "LUMIPAD|2|SAVER:READY"
                : "LUMIPAD|2|SAVER:EMPTY");
    }

    return rc;
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

    lumi_diag_report('I', "Firmware diagnostics online");

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
