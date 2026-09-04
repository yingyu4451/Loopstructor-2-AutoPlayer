<script setup lang="ts">
import { computed, onMounted, ref, toRaw, watch } from 'vue'
import { Activity, Bot, OctagonX, Pause, Play, RefreshCw, ShieldAlert } from '../icons'
import { useAppStore } from '../stores/app'
import { useUiStore } from '../stores/ui'
import type { ManagerSettings } from '../types'

const store = useAppStore()
const ui = useUiStore()
const cloneSettings = (settings: ManagerSettings) => structuredClone(toRaw(settings))
const draft = ref<ManagerSettings>()
const setupLoading = ref(false)

watch(() => store.settings, (settings) => {
  if (settings && !draft.value) draft.value = cloneSettings(settings)
}, { immediate: true })

watch(() => store.connected, async (connected) => {
  if (connected && !store.automationSetup) await refreshSetup()
})

const status = computed(() => store.snapshot?.status)
const runState = computed(() => String(status.value?.runState ?? 'standby').toLowerCase())
const isRunning = computed(() => runState.value === 'running')
const isPaused = computed(() => runState.value === 'paused')
const isActive = computed(() => isRunning.value || isPaused.value)
const modes = computed(() => store.automationSetup?.modes ?? [])
const characters = computed(() => store.automationSetup?.characters ?? [])
const showCharacter = computed(() => draft.value?.gameMode !== 'random' && !draft.value?.continueExistingProfile)
const selectedMode = computed(() => modes.value.find(mode => mode.mode.toLowerCase() === draft.value?.gameMode?.toLowerCase()))
const setupAllowsStart = computed(() => {
  if (draft.value?.continueExistingProfile) return true
  if (!selectedMode.value?.available) return false
  return draft.value?.gameMode === 'random' || characters.value.some(item => item.cfgIndex === draft.value?.characterCfgIndex)
})
const canStart = computed(() => store.connected
  && !status.value?.needsProcessRestart
  && ['standby', 'completed', 'faulted'].includes(runState.value)
  && setupAllowsStart.value)
const timeline = computed(() => [...(status.value?.timeline ?? [])].slice(-120).reverse())
const speedChoice = computed({
  get: () => draft.value?.overrideGameSpeed ? String(draft.value.speedState) : 'native',
  set: (value: string) => {
    if (!draft.value) return
    draft.value.overrideGameSpeed = value !== 'native'
    draft.value.speedState = value === 'native' ? 0 : Number(value)
  },
})

async function refreshSetup() {
  if (!store.connected || setupLoading.value) return
  setupLoading.value = true
  try {
    const response = await store.refreshAutomationSetup()
    if (!response?.success || !draft.value) return
    const availableModes = modes.value.filter(mode => mode.available)
    if (!availableModes.some(mode => mode.mode.toLowerCase() === draft.value!.gameMode.toLowerCase())) {
      draft.value.gameMode = availableModes[0]?.mode ?? 'common'
    }
    if (characters.value.length > 0 && !characters.value.some(item => item.cfgIndex === draft.value!.characterCfgIndex)) {
      draft.value.characterCfgIndex = characters.value[0].cfgIndex
    }
  } finally {
    setupLoading.value = false
  }
}

async function start() {
  if (!draft.value) return
  await store.saveSettings(draft.value, false)
  await store.startAutomation()
}

onMounted(refreshSetup)
</script>

<template>
  <div class="page-grid autoplay-page">
    <section class="page-heading">
      <div><span class="eyebrow">AUTOMATION</span><h1>自动游玩</h1></div>
      <button class="btn btn-outline btn-sm button secondary compact" :disabled="!store.connected || setupLoading || ui.busy" @click="refreshSetup">
        <RefreshCw :size="16" />刷新可玩内容
      </button>
    </section>

    <section class="automation-warning" role="status">
      <ShieldAlert :size="22" />
      <div><strong>自动游玩尚未完成</strong><span>当前版本允许使用，但仍可能遇到流程中断或策略错误，请避免用于重要存档。</span></div>
    </section>

    <div class="automation-workspace">
      <section class="card mechanical-section automation-config">
        <header class="section-heading compact-heading">
          <div class="heading-with-icon"><Bot :size="21" /><div><h2>运行配置</h2><p>可玩模式和角色直接读取当前已连接的游戏。</p></div></div>
          <span class="status-badge" :class="{ online: store.connected, warning: !store.connected }">{{ store.connected ? '游戏已连接' : '等待游戏连接' }}</span>
        </header>
        <div v-if="draft" class="automation-form" :class="{ locked: isActive }">
          <label><span>游戏模式</span><select v-model="draft.gameMode" class="select select-bordered" name="game-mode" autocomplete="off" :disabled="isActive"><option v-for="mode in modes" :key="mode.mode" :value="mode.mode" :disabled="!mode.available" :title="mode.reason">{{ mode.displayName }}{{ mode.available ? '' : ' · 不可用' }}</option></select></label>
          <label><span>存档流程</span><select v-model="draft.continueExistingProfile" class="select select-bordered" name="profile-flow" autocomplete="off" :disabled="isActive"><option :value="false">开始新游戏</option><option :value="true">继续当前存档</option></select></label>
          <label v-if="showCharacter"><span>角色</span><select v-model.number="draft.characterCfgIndex" class="select select-bordered" name="character" autocomplete="off" :disabled="isActive"><option v-for="character in characters" :key="character.cfgIndex" :value="character.cfgIndex">{{ character.displayName }}</option></select></label>
          <label><span>游戏速度</span><select v-model="speedChoice" class="select select-bordered" name="game-speed" autocomplete="off" :disabled="isActive"><option value="native">跟随游戏</option><option value="0">1×</option><option value="1">2×</option><option value="2">3×</option></select></label>
          <label><span>最长运行</span><div class="number-suffix"><input v-model.number="draft.maxRunMinutes" class="input input-bordered" name="max-run-minutes" autocomplete="off" type="number" inputmode="numeric" min="5" max="480" :disabled="isActive" /><span>分钟</span></div></label>
          <label><span>决策优先</span><select v-model="draft.decisionPriority" class="select select-bordered" name="decision-priority" autocomplete="off" :disabled="isActive"><option value="vehicleRewards">优先拿三星车</option><option value="catapultPoints">优先拿弹射点</option><option value="relics">优先拿遗物</option></select></label>
          <label class="automation-toggle"><input v-model="draft.skipStory" class="checkbox checkbox-success" name="skip-story" type="checkbox" :disabled="isActive" /><span class="switch-track"><span /></span><strong>跳过剧情</strong></label>
        </div>
        <div class="automation-actions">
          <button class="btn btn-primary button primary" :disabled="!canStart || ui.busy" @click="start"><Play :size="17" />开始</button>
          <button class="btn btn-outline button secondary" :disabled="!isRunning || ui.busy" @click="store.pauseAutomation"><Pause :size="17" />暂停</button>
          <button class="btn btn-outline button secondary" :disabled="!isPaused || ui.busy" @click="store.resumeAutomation"><Play :size="17" />继续</button>
          <button class="btn btn-error button danger" :disabled="!isActive || ui.busy" @click="store.stopAutomation"><OctagonX :size="17" />停止</button>
        </div>
      </section>

      <section class="card mechanical-section automation-runtime">
        <header class="section-heading compact-heading">
          <div class="heading-with-icon"><Activity :size="21" /><div><h2>运行轨迹</h2><p>{{ status?.stageDetail || '开始后将在这里显示当前步骤与决策。' }}</p></div></div>
          <span class="status-badge" :class="{ online: isActive, warning: runState === 'faulted' }">{{ status?.runState || 'standby' }}</span>
        </header>
        <div class="automation-facts">
          <div><span>阶段</span><strong>{{ status?.stage || '-' }}</strong></div>
          <div><span>结果</span><strong>{{ status?.outcome || '-' }}</strong></div>
          <div><span>波次</span><strong>{{ status ? `${status.wavesCompleted}/${status.wavesStarted}` : '-' }}</strong></div>
        </div>
        <div class="automation-timeline">
          <article v-for="(entry, index) in timeline" :key="`${entry.timestampUtc}-${index}`" :class="`timeline-${entry.kind}`">
            <time>{{ new Date(entry.timestampUtc).toLocaleTimeString('zh-CN', { hour12: false }) }}</time>
            <div><strong>{{ entry.stage }}</strong><p>{{ entry.message }}</p></div>
          </article>
          <p v-if="timeline.length === 0" class="empty-state">尚无自动游玩轨迹。</p>
        </div>
      </section>
    </div>
  </div>
</template>
