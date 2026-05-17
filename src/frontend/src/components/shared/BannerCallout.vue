<template>
  <div class="banner-callout" :class="`banner-${tone}`">
    <span class="banner-callout-icon" :class="iconPulse ? 'banner-pulse' : null">
      <slot name="icon" />
    </span>
    <div class="banner-callout-text">
      <strong>{{ title }}</strong>
      <span v-if="subtitle || $slots.subtitle">
        <slot name="subtitle">{{ subtitle }}</slot>
      </span>
    </div>
    <slot name="action" />
  </div>
</template>

<script setup lang="ts">
/**
 * Plan §6 shared primitive — banner callout. Wraps the recurring
 * "icon + title strong + subtitle + action button" shape used by Easy
 * Dashboard readiness signals (apache stopped, update available,
 * backup stale, etc.).
 *
 * Tones map to el-color-* palette:
 *   warning  — yellow, "something needs attention"
 *   info     — blue/primary accent, informational nudge
 *   neutral  — gray, low-priority FYI
 */
defineOptions({ name: 'BannerCallout' })
defineProps<{
  title: string
  subtitle?: string
  tone: 'warning' | 'info' | 'neutral'
  iconPulse?: boolean
}>()
</script>

<style scoped>
.banner-callout {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 14px 18px;
  margin: 4px auto 0;
  width: 100%;
  border-radius: var(--wdc-radius);
}
.banner-warning {
  background: color-mix(in srgb, var(--el-color-warning) 14%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-warning) 40%, transparent);
}
.banner-info {
  background: color-mix(in srgb, var(--el-color-primary) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-primary) 40%, transparent);
}
.banner-neutral {
  background: color-mix(in srgb, var(--el-color-info) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-info) 40%, transparent);
}

.banner-callout-icon {
  font-size: 28px;
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
}
.banner-warning .banner-callout-icon { color: var(--el-color-warning); }
.banner-info .banner-callout-icon { color: var(--el-color-primary); }
.banner-neutral .banner-callout-icon { color: var(--el-color-info); }

.banner-callout-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.banner-callout-text strong { color: var(--wdc-text); font-size: 0.95rem; }
.banner-callout-text span { color: var(--wdc-text-2); font-size: 0.84rem; }

.banner-pulse {
  animation: wdc-banner-pulse 1.8s ease-in-out infinite;
}
@keyframes wdc-banner-pulse {
  0%, 100% { opacity: 1; transform: scale(1); }
  50%      { opacity: 0.6; transform: scale(1.1); }
}
</style>
