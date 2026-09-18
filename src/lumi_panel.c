/* SPDX-License-Identifier: MIT */
#include <errno.h>
#include <zephyr/device.h>
#include <zephyr/drivers/gpio.h>
#include <zephyr/drivers/spi.h>
#include <zephyr/logging/log.h>
#include <zephyr/kernel.h>
#include <lvgl.h>
#include "lumi_panel.h"

LOG_MODULE_REGISTER(lumi_panel, CONFIG_ZMK_LOG_LEVEL);
#define PANEL DT_CHOSEN(zephyr_display)
static const struct spi_dt_spec bus =
    SPI_DT_SPEC_GET(PANEL, SPI_OP_MODE_MASTER | SPI_WORD_SET(8), 0);
static const struct gpio_dt_spec dc = GPIO_DT_SPEC_GET(PANEL, cmd_data_gpios);

static int lumi_panel_write_data(const uint8_t *data, size_t len) {
    if (!data || len == 0U) {
        return -EINVAL;
    }

    int err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        return err;
    }

    struct spi_buf buffer = {.buf = (void *)data, .len = len};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    return spi_write_dt(&bus, &buffers);
}


static volatile bool async_flush_busy;

static int lumi_panel_send_command(uint8_t command) {
    struct spi_buf buffer = {.buf = &command, .len = 1U};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err != 0) {
        return err;
    }

    return spi_write_dt(&bus, &buffers);
}

static int lumi_panel_send_data(const uint8_t *data, size_t len) {
    if (!data || len == 0U) {
        return -EINVAL;
    }

    struct spi_buf buffer = {.buf = (void *)data, .len = len};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        return err;
    }

    return spi_write_dt(&bus, &buffers);
}

static int lumi_panel_set_window(const lv_area_t *area) {
    if (!area) {
        return -EINVAL;
    }

    uint16_t x1 = (uint16_t)area->x1;
    uint16_t x2 = (uint16_t)area->x2;
    uint16_t y1 = (uint16_t)(area->y1 + 34);
    uint16_t y2 = (uint16_t)(area->y2 + 34);

    uint8_t data[4];

    int err = lumi_panel_send_command(0x2A); /* CASET */
    if (err != 0) {
        return err;
    }

    data[0] = (uint8_t)(x1 >> 8);
    data[1] = (uint8_t)x1;
    data[2] = (uint8_t)(x2 >> 8);
    data[3] = (uint8_t)x2;
    err = lumi_panel_send_data(data, sizeof(data));
    if (err != 0) {
        return err;
    }

    err = lumi_panel_send_command(0x2B); /* RASET */
    if (err != 0) {
        return err;
    }

    data[0] = (uint8_t)(y1 >> 8);
    data[1] = (uint8_t)y1;
    data[2] = (uint8_t)(y2 >> 8);
    data[3] = (uint8_t)y2;
    err = lumi_panel_send_data(data, sizeof(data));
    if (err != 0) {
        return err;
    }

    return lumi_panel_send_command(0x2C); /* RAMWR */
}

static void lumi_panel_async_done(const struct device *dev,
                                  int result,
                                  void *userdata) {
    ARG_UNUSED(dev);

    lv_disp_drv_t *drv = (lv_disp_drv_t *)userdata;
    async_flush_busy = false;

    if (result != 0) {
        LOG_ERR("Async TFT payload failed: %d", result);
    }

    lv_disp_flush_ready(drv);
}

static void lumi_panel_async_flush(lv_disp_drv_t *drv,
                                   const lv_area_t *area,
                                   lv_color_t *color_p) {
    if (!drv || !area || !color_p) {
        if (drv) {
            lv_disp_flush_ready(drv);
        }
        return;
    }

    if (async_flush_busy) {
        LOG_ERR("Unexpected overlapping TFT flush");
        lv_disp_flush_ready(drv);
        return;
    }

    int err = lumi_panel_set_window(area);
    if (err != 0) {
        LOG_ERR("TFT window setup failed: %d", err);
        lv_disp_flush_ready(drv);
        return;
    }

    uint32_t width = (uint32_t)(area->x2 - area->x1 + 1);
    uint32_t height = (uint32_t)(area->y2 - area->y1 + 1);
    size_t len = (size_t)width * height * sizeof(lv_color_t);

    struct spi_buf buffer = {.buf = color_p, .len = len};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    err = gpio_pin_set_dt(&dc, 0);
    if (err != 0) {
        LOG_ERR("TFT D/C setup failed: %d", err);
        lv_disp_flush_ready(drv);
        return;
    }

    async_flush_busy = true;
    err = spi_transceive_cb(bus.bus,
                            &bus.config,
                            &buffers,
                            NULL,
                            lumi_panel_async_done,
                            drv);
    if (err != 0) {
        async_flush_busy = false;
        LOG_ERR("Async TFT transfer start failed: %d", err);
        lv_disp_flush_ready(drv);
    }
}

int lumi_panel_enable_async_flush(void) {
    lv_disp_t *disp = lv_disp_get_default();
    if (!disp || !disp->driver) {
        return -ENODEV;
    }

    disp->driver->flush_cb = lumi_panel_async_flush;
    LOG_INF("LVGL TFT flush switched to asynchronous SPI");
    return 0;
}

int lumi_panel_init(void) {
    /* This ST7789 panel variant needs display inversion enabled for
     * normal black/white polarity. D/C is active-low: logical 1 means
     * command (physical 0). BL is tied to 3V3.
     */
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }
    uint8_t command = 0x21; /* INVON */
    struct spi_buf buffer = {.buf = &command, .len = sizeof(command)};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};
    int err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err) {
        LOG_ERR("Panel inversion setup failed: %d", err);
        return err;
    }

    /* Program PORCTRL explicitly as well as through devicetree so the
     * runtime timing does not depend on the display driver's init sequence.
     * FPA=BPA=0x6C; remaining porch bytes match the panel baseline.
     */
    command = 0xB2;
    err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err != 0) {
        LOG_ERR("Panel PORCTRL command failed: %d", err);
        return err;
    }

    const uint8_t porch[5] = {0x6C, 0x6C, 0x00, 0x33, 0x33};
    err = lumi_panel_write_data(porch, sizeof(porch));
    if (err != 0) {
        LOG_ERR("Panel PORCTRL data failed: %d", err);
        return err;
    }

    /* FRCTRL2 (C6h), RTNA=0x1F. Datasheet nominal timing is about 25 Hz
     * with FPA=BPA=0x6C and a 10 MHz internal oscillator.
     */
    command = 0xC6;
    err = gpio_pin_set_dt(&dc, 1);
    if (err == 0) {
        err = spi_write_dt(&bus, &buffers);
    }
    if (err != 0) {
        LOG_ERR("Panel FRCTRL2 command failed: %d", err);
        return err;
    }

    const uint8_t frctrl2 = 0x1F;
    err = lumi_panel_write_data(&frctrl2, 1U);
    if (err) {
        LOG_ERR("Panel FRCTRL2 data failed: %d", err);
    }

    return err;
}

int lumi_panel_set_sleep(bool sleeping) {
    if (!spi_is_ready_dt(&bus) || !gpio_is_ready_dt(&dc)) {
        return -ENODEV;
    }

    uint8_t command = sleeping ? 0x28 : 0x11;
    struct spi_buf buffer = {.buf = &command, .len = sizeof(command)};
    const struct spi_buf_set buffers = {.buffers = &buffer, .count = 1};

    int err = gpio_pin_set_dt(&dc, 1);
    if (err != 0) {
        return err;
    }

    err = spi_write_dt(&bus, &buffers);
    if (err != 0) {
        return err;
    }

    if (sleeping) {
        k_msleep(10);
        command = 0x10; /* SLPIN */
        err = spi_write_dt(&bus, &buffers);
    } else {
        k_msleep(120);
        command = 0x29; /* DISPON */
        err = spi_write_dt(&bus, &buffers);
    }

    return err;
}
