<template>
  <span class="service-icon" :class="{ inactive: !active }">
    <!-- Apache feather -->
    <svg v-if="service === 'apache'" viewBox="0 0 24 24" fill="currentColor">
      <path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 15h-2v-2h2v2zm0-4h-2V7h2v6zm4 4h-2v-2h2v2zm0-4h-2V7h2v6z" opacity="0.8"/>
      <path d="M12 2L8 8l4 6-4 6 4 2 4-2-4-6 4-6-4-6z" fill="currentColor"/>
    </svg>
    <!-- PHP -->
    <svg v-else-if="service === 'php'" viewBox="0 0 24 24" fill="currentColor">
      <ellipse cx="12" cy="12" rx="11" ry="7" fill="none" stroke="currentColor" stroke-width="1.5"/>
      <text x="12" y="15" text-anchor="middle" font-size="9" font-weight="bold" fill="currentColor">php</text>
    </svg>
    <!-- MySQL -->
    <svg v-else-if="service === 'mysql'" viewBox="0 0 24 24" fill="currentColor">
      <circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="1.5"/>
      <text x="12" y="15" text-anchor="middle" font-size="7" font-weight="bold" fill="currentColor">SQL</text>
    </svg>
    <!-- Redis -->
    <svg v-else-if="service === 'redis'" viewBox="0 0 24 24" fill="currentColor">
      <path d="M12 3L2 9l10 6 10-6-10-6zM2 15l10 6 10-6M2 12l10 6 10-6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/>
    </svg>
    <!-- Mailpit -->
    <svg v-else-if="service === 'mailpit'" viewBox="0 0 24 24" fill="currentColor">
      <rect x="3" y="5" width="18" height="14" rx="2" fill="none" stroke="currentColor" stroke-width="1.5"/>
      <path d="M3 7l9 6 9-6" fill="none" stroke="currentColor" stroke-width="1.5"/>
    </svg>
    <!-- Default circle -->
    <svg v-else viewBox="0 0 24 24" fill="currentColor">
      <circle cx="12" cy="12" r="8" fill="currentColor"/>
    </svg>
  </span>
</template>

<script setup lang="ts">
defineProps<{
  service: string
  active: boolean
}>()
</script>

<style scoped>
.service-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  transition: all 0.15s;
}

.service-icon svg {
  width: 100%;
  height: 100%;
}

/* Color mapping per service when active */
.service-icon:not(.inactive) { color: var(--wdc-accent); }

/* Service-specific colors when active */
:deep([data-service="apache"]) .service-icon:not(.inactive) { color: #e44d26; }
:deep([data-service="php"]) .service-icon:not(.inactive) { color: #8892bf; }
:deep([data-service="mysql"]) .service-icon:not(.inactive) { color: #4479a1; }
:deep([data-service="redis"]) .service-icon:not(.inactive) { color: #dc382d; }
:deep([data-service="mailpit"]) .service-icon:not(.inactive) { color: #22c55e; }

.service-icon.inactive {
  color: var(--wdc-text-3);
  opacity: 0.5;
}
</style>
