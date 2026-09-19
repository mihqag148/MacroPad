/* SPDX-License-Identifier: MIT */

#define DT_DRV_COMPAT zmk_behavior_lumi_action

#include <zephyr/device.h>
#include <zephyr/devicetree.h>
#include <zephyr/kernel.h>

#include <drivers/behavior.h>
#include <zmk/behavior.h>

#include "lumi_app_link.h"
#include "lumi_diag.h"

#if DT_HAS_COMPAT_STATUS_OKAY(DT_DRV_COMPAT)

#if IS_ENABLED(CONFIG_ZMK_BEHAVIOR_METADATA)
#define LUMI_ACTION_META(n)     {.display_name = "Action " #n,      .type = BEHAVIOR_PARAMETER_VALUE_TYPE_VALUE,      .value = n}

static const struct behavior_parameter_value_metadata lumi_action_values[] = {
    LUMI_ACTION_META(1),  LUMI_ACTION_META(2),
    LUMI_ACTION_META(3),  LUMI_ACTION_META(4),
    LUMI_ACTION_META(5),  LUMI_ACTION_META(6),
    LUMI_ACTION_META(7),  LUMI_ACTION_META(8),
    LUMI_ACTION_META(9),  LUMI_ACTION_META(10),
    LUMI_ACTION_META(11), LUMI_ACTION_META(12),
    LUMI_ACTION_META(13), LUMI_ACTION_META(14),
    LUMI_ACTION_META(15), LUMI_ACTION_META(16),
    LUMI_ACTION_META(17), LUMI_ACTION_META(18),
    LUMI_ACTION_META(19), LUMI_ACTION_META(20),
    LUMI_ACTION_META(21), LUMI_ACTION_META(22),
    LUMI_ACTION_META(23), LUMI_ACTION_META(24),
    LUMI_ACTION_META(25), LUMI_ACTION_META(26),
    LUMI_ACTION_META(27), LUMI_ACTION_META(28),
    LUMI_ACTION_META(29), LUMI_ACTION_META(30),
    LUMI_ACTION_META(31), LUMI_ACTION_META(32),
};

static const struct behavior_parameter_metadata_set lumi_action_metadata_sets[] = {
    {
        .param1_values = lumi_action_values,
        .param1_values_len = ARRAY_SIZE(lumi_action_values),
    },
};

static const struct behavior_parameter_metadata lumi_action_metadata = {
    .sets_len = ARRAY_SIZE(lumi_action_metadata_sets),
    .sets = lumi_action_metadata_sets,
};
#endif

static int lumi_action_binding_pressed(
    struct zmk_behavior_binding *binding,
    struct zmk_behavior_binding_event event) {

    uint16_t action_id = (uint16_t)binding->param1;
    if (action_id == 0U || action_id > 32U) {
        return -ENOTSUP;
    }

    lumi_app_action_emit(action_id, (uint32_t)event.position);
    lumi_diag_report(
        'I',
        "Lumi Action id=%u pos=%u",
        (unsigned int)action_id,
        (unsigned int)event.position);

    return ZMK_BEHAVIOR_OPAQUE;
}

static int lumi_action_binding_released(
    struct zmk_behavior_binding *binding,
    struct zmk_behavior_binding_event event) {
    ARG_UNUSED(binding);
    ARG_UNUSED(event);
    return ZMK_BEHAVIOR_OPAQUE;
}

static const struct behavior_driver_api lumi_action_driver_api = {
    .binding_pressed = lumi_action_binding_pressed,
    .binding_released = lumi_action_binding_released,
    .locality = BEHAVIOR_LOCALITY_GLOBAL,
#if IS_ENABLED(CONFIG_ZMK_BEHAVIOR_METADATA)
    .parameter_metadata = &lumi_action_metadata,
#endif
};

BEHAVIOR_DT_INST_DEFINE(
    0,
    NULL,
    NULL,
    NULL,
    NULL,
    POST_KERNEL,
    CONFIG_KERNEL_INIT_PRIORITY_DEFAULT,
    &lumi_action_driver_api);

#endif
