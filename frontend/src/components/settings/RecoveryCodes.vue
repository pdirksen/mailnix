<script setup lang="ts">
import { Button } from '@/components/ui/button'
import { CheckIcon, CopyIcon, DownloadIcon } from '@lucide/vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'

/** Freshly created recovery codes: shown once, to copy or download. */
const props = defineProps<{ codes: string[] }>()

const { t } = useI18n()
const copied = ref(false)

function asText() {
  return `${t('settings.twoFactor.codesFileHeader')}\n\n${props.codes.join('\n')}\n`
}

async function copy() {
  try {
    await navigator.clipboard.writeText(asText())
    copied.value = true
    setTimeout(() => (copied.value = false), 2000)
  } catch {
    copied.value = false
  }
}

function download() {
  const url = URL.createObjectURL(new Blob([asText()], { type: 'text/plain' }))
  const link = Object.assign(document.createElement('a'), { href: url, download: 'mailnix-recovery-codes.txt' })
  link.click()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <div class="grid gap-3">
    <ol class="bg-muted/50 grid grid-cols-2 gap-x-6 gap-y-1.5 rounded-lg border p-4 font-mono text-sm" data-testid="recovery-codes">
      <li v-for="code in codes" :key="code" class="tabular-nums">{{ code }}</li>
    </ol>
    <div class="flex flex-wrap gap-2">
      <Button type="button" variant="outline" size="sm" @click="copy">
        <CheckIcon v-if="copied" />
        <CopyIcon v-else />
        {{ copied ? $t('settings.twoFactor.copied') : $t('settings.twoFactor.copyCodes') }}
      </Button>
      <Button type="button" variant="outline" size="sm" @click="download">
        <DownloadIcon />
        {{ $t('settings.twoFactor.downloadCodes') }}
      </Button>
    </div>
  </div>
</template>
