<script lang="ts">
  import { Play, Square, RotateCcw, Cpu, MemoryStick, Wifi } from 'lucide-svelte';
  import type { Service } from '$lib/stores/services';
  import { startService, stopService, restartService } from '$lib/stores/services';

  interface Props {
    service: Service;
  }

  const { service }: Props = $props();

  // ---------------------------------------------------------------- //
  // Derived                                                           //
  // ---------------------------------------------------------------- //

  const isRunning = $derived(service.status === 'running');
  const isStopped = $derived(service.status === 'stopped');
  const isTransitioning = $derived(
    service.status === 'starting' || service.status === 'stopping'
  );

  const statusLabel = $derived({
    running: 'Running',
    stopped: 'Stopped',
    starting: 'Starting…',
    stopping: 'Stopping…',
    warning: 'Warning',
    unknown: 'Unknown',
  }[service.status] ?? service.status);

  const memoryFormatted = $derived(() => {
    const mb = service.metrics.memory / 1_048_576;
    if (mb < 1) return `${Math.round(service.metrics.memory / 1024)} KB`;
    if (mb < 1024) return `${mb.toFixed(1)} MB`;
    return `${(mb / 1024).toFixed(2)} GB`;
  });

  const uptimeFormatted = $derived(() => {
    if (!service.uptime) return null;
    const h = Math.floor(service.uptime / 3600);
    const m = Math.floor((service.uptime % 3600) / 60);
    if (h > 0) return `${h}h ${m}m`;
    if (m > 0) return `${m}m`;
    return `${service.uptime}s`;
  });

  // ---------------------------------------------------------------- //
  // Actions                                                           //
  // ---------------------------------------------------------------- //

  async function handleStart() {
    await startService(service.id);
  }

  async function handleStop() {
    await stopService(service.id);
  }

  async function handleRestart() {
    await restartService(service.id);
  }
</script>

<article class="service-card" class:running={isRunning} class:stopped={isStopped} class:warning={service.status === 'warning'}>

  <!-- Header -->
  <header class="card-header">
    <div class="card-title">
      <div class="status-dot" aria-hidden="true"></div>
      <span class="service-name">{service.displayName}</span>
      {#if service.port}
        <span class="port-badge">:{service.port}</span>
      {/if}
    </div>
    <span class="version-badge">{service.version}</span>
  </header>

  <!-- Status row -->
  <div class="status-row">
    <span class="status-label" class:transitioning={isTransitioning}>
      {statusLabel}
    </span>
    {#if uptimeFormatted()}
      <span class="uptime">up {uptimeFormatted()}</span>
    {/if}
    {#if service.pid}
      <span class="pid font-mono">PID {service.pid}</span>
    {/if}
  </div>

  <!-- Metrics row -->
  <div class="metrics-row">
    <div class="metric">
      <Cpu size={12} />
      <div class="metric-bar-wrap">
        <div
          class="metric-bar"
          style:width="{Math.min(service.metrics.cpu, 100)}%"
          class:high={service.metrics.cpu > 75}
          class:warn={service.metrics.cpu > 50 && service.metrics.cpu <= 75}
        ></div>
      </div>
      <span class="metric-value">{service.metrics.cpu.toFixed(1)}%</span>
    </div>
    <div class="metric">
      <MemoryStick size={12} />
      <span class="metric-value">{memoryFormatted()}</span>
    </div>
    {#if service.metrics.connections !== undefined}
      <div class="metric">
        <Wifi size={12} />
        <span class="metric-value">{service.metrics.connections}</span>
      </div>
    {/if}
  </div>

  <!-- Error notice -->
  {#if service.error}
    <div class="error-notice">{service.error}</div>
  {/if}

  <!-- Actions -->
  <footer class="card-actions">
    <button
      class="btn-icon btn-start"
      onclick={handleStart}
      disabled={isRunning || isTransitioning}
      aria-label="Start {service.displayName}"
      title="Start"
    >
      <Play size={13} fill="currentColor" />
    </button>

    <button
      class="btn-icon btn-stop"
      onclick={handleStop}
      disabled={isStopped || isTransitioning}
      aria-label="Stop {service.displayName}"
      title="Stop"
    >
      <Square size={13} fill="currentColor" />
    </button>

    <button
      class="btn-icon btn-restart"
      onclick={handleRestart}
      disabled={isStopped || isTransitioning}
      aria-label="Restart {service.displayName}"
      title="Restart"
    >
      <RotateCcw size={13} class:spinning={isTransitioning} />
    </button>
  </footer>

</article>

<style>
  .service-card {
    background: var(--bg-surface);
    border: 1px solid var(--border-default);
    border-radius: var(--radius-lg);
    padding: var(--space-4);
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    transition: border-color var(--transition-fast), box-shadow var(--transition-fast);
    min-width: 200px;
  }

  .service-card:hover {
    border-color: var(--border-strong);
    box-shadow: var(--shadow-sm);
  }

  .service-card.running {
    border-color: rgba(34, 197, 94, 0.2);
  }

  .service-card.stopped {
    border-color: rgba(239, 68, 68, 0.15);
  }

  .service-card.warning {
    border-color: rgba(245, 158, 11, 0.2);
  }

  /* Header */
  .card-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-2);
  }

  .card-title {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    min-width: 0;
  }

  .status-dot {
    width: 8px;
    height: 8px;
    border-radius: var(--radius-full);
    flex-shrink: 0;
    background: var(--text-muted);
    transition: background var(--transition-normal);
  }

  .running .status-dot  { background: var(--status-running); box-shadow: 0 0 6px var(--status-running); }
  .stopped .status-dot  { background: var(--status-stopped); }
  .warning .status-dot  { background: var(--status-warning); box-shadow: 0 0 6px var(--status-warning); }

  .service-name {
    font-size: var(--font-size-md);
    font-weight: 600;
    color: var(--text-primary);
    white-space: nowrap;
  }

  .port-badge {
    font-family: var(--font-mono);
    font-size: var(--font-size-xs);
    color: var(--text-muted);
  }

  .version-badge {
    font-family: var(--font-mono);
    font-size: var(--font-size-xs);
    color: var(--text-secondary);
    background: var(--bg-elevated);
    border: 1px solid var(--border-default);
    border-radius: var(--radius-sm);
    padding: 2px 6px;
    white-space: nowrap;
    flex-shrink: 0;
  }

  /* Status row */
  .status-row {
    display: flex;
    align-items: center;
    gap: var(--space-3);
  }

  .status-label {
    font-size: var(--font-size-sm);
    font-weight: 500;
    color: var(--text-secondary);
  }

  .running .status-label  { color: var(--status-running); }
  .stopped .status-label  { color: var(--status-stopped); }
  .warning .status-label  { color: var(--status-warning); }

  .status-label.transitioning {
    animation: pulse 1s ease-in-out infinite;
    color: var(--text-secondary);
  }

  .uptime, .pid {
    font-size: var(--font-size-xs);
    color: var(--text-muted);
  }

  /* Metrics */
  .metrics-row {
    display: flex;
    gap: var(--space-3);
    flex-wrap: wrap;
  }

  .metric {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    color: var(--text-muted);
    font-size: var(--font-size-xs);
  }

  .metric-bar-wrap {
    width: 48px;
    height: 4px;
    background: var(--bg-elevated);
    border-radius: var(--radius-full);
    overflow: hidden;
  }

  .metric-bar {
    height: 100%;
    background: var(--accent-blue);
    border-radius: var(--radius-full);
    transition: width var(--transition-normal);
  }

  .metric-bar.warn { background: var(--status-warning); }
  .metric-bar.high { background: var(--status-stopped); }

  .metric-value {
    color: var(--text-secondary);
    font-family: var(--font-mono);
    font-size: var(--font-size-xs);
  }

  /* Error */
  .error-notice {
    font-size: var(--font-size-xs);
    color: var(--status-stopped);
    background: var(--status-stopped-bg);
    border: 1px solid rgba(239, 68, 68, 0.2);
    border-radius: var(--radius-sm);
    padding: var(--space-1) var(--space-2);
    line-height: 1.4;
  }

  /* Actions */
  .card-actions {
    display: flex;
    gap: var(--space-2);
    padding-top: var(--space-1);
    border-top: 1px solid var(--border-default);
  }

  .btn-icon {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    border-radius: var(--radius-md);
    border: 1px solid var(--border-default);
    background: var(--bg-elevated);
    color: var(--text-secondary);
    cursor: pointer;
    transition: background var(--transition-fast), color var(--transition-fast), border-color var(--transition-fast);
  }

  .btn-icon:hover:not(:disabled) {
    border-color: var(--border-strong);
    color: var(--text-primary);
  }

  .btn-icon:disabled {
    opacity: 0.35;
    cursor: not-allowed;
  }

  .btn-start:hover:not(:disabled) {
    background: var(--status-running-bg);
    border-color: var(--status-running);
    color: var(--status-running);
  }

  .btn-stop:hover:not(:disabled) {
    background: var(--status-stopped-bg);
    border-color: var(--status-stopped);
    color: var(--status-stopped);
  }

  .btn-restart:hover:not(:disabled) {
    background: var(--accent-blue-dim);
    border-color: var(--accent-blue);
    color: var(--accent-blue);
  }

  @keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
  }

  :global(.spinning) {
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    from { transform: rotate(0deg); }
    to   { transform: rotate(360deg); }
  }
</style>
