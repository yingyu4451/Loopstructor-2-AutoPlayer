import type { Directive } from 'vue'

const timers = new WeakMap<HTMLElement, number>()
let tooltip: HTMLDivElement | undefined

function hide(element: HTMLElement): void {
  const timer = timers.get(element)
  if (timer) window.clearTimeout(timer)
  timers.delete(element)
  tooltip?.remove()
  tooltip = undefined
}

export const tooltipDirective: Directive<HTMLElement, string> = {
  mounted(element, binding) {
    const show = () => {
      hide(element)
      if (!binding.value?.trim()) return
      const timer = window.setTimeout(() => {
        const rect = element.getBoundingClientRect()
        tooltip = document.createElement('div')
        tooltip.className = 'mechanical-tooltip'
        tooltip.textContent = binding.value
        document.body.appendChild(tooltip)
        const width = tooltip.offsetWidth
        const left = Math.min(window.innerWidth - width - 12, Math.max(12, rect.left + rect.width / 2 - width / 2))
        const below = rect.bottom + 10
        tooltip.style.left = `${left}px`
        tooltip.style.top = `${below + tooltip.offsetHeight < window.innerHeight ? below : Math.max(12, rect.top - tooltip.offsetHeight - 10)}px`
      }, 1000)
      timers.set(element, timer)
    }
    element.addEventListener('mouseenter', show)
    element.addEventListener('mouseleave', () => hide(element))
    element.addEventListener('click', () => hide(element))
    element.addEventListener('wheel', () => hide(element), { passive: true })
  },
  beforeUnmount: hide,
}
