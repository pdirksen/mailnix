<script setup lang="ts">
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { type Profile, useProfileStore } from '@/stores/profile'
import { AtSignIcon, Loader2Icon, LockIcon, PlusIcon, XIcon } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { z } from 'zod'

const props = defineProps<{ profile: Profile }>()

const { t } = useI18n()
const store = useProfileStore()
const { describeRequestError } = useConnectionErrors()

const MAX_ADDRESSES = 50
const newAddress = ref('')
const error = ref<string>()
const busy = ref(false)

const known = computed(() => new Set([...props.profile.addresses, ...props.profile.automaticAddresses]))

async function saveAll(addresses: string[], message: string) {
  busy.value = true
  try {
    await store.saveAddresses(addresses)
    toast.success(message)
    return true
  } catch (failure) {
    toast.error(describeRequestError(failure).message)
    return false
  } finally {
    busy.value = false
  }
}

async function add() {
  const address = newAddress.value.trim().toLowerCase()
  if (!z.email().safeParse(address).success) {
    error.value = t('validation.email')
    return
  }
  if (known.value.has(address)) {
    error.value = t('settings.addresses.duplicate')
    return
  }
  if (props.profile.addresses.length >= MAX_ADDRESSES) {
    error.value = t('settings.addresses.tooMany', { max: MAX_ADDRESSES })
    return
  }
  error.value = undefined
  if (await saveAll([...props.profile.addresses, address], t('settings.addresses.added', { address }))) newAddress.value = ''
}

async function remove(address: string) {
  await saveAll(
    props.profile.addresses.filter((a) => a !== address),
    t('settings.addresses.removed', { address }),
  )
}
</script>

<template>
  <div class="grid gap-6">
    <Card>
      <CardHeader>
        <CardTitle>{{ $t('settings.addresses.title') }}</CardTitle>
        <CardDescription>{{ $t('settings.addresses.description') }}</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-5">
        <ul class="grid gap-2 text-sm" role="list">
          <li v-for="use in ['onlyCc', 'alias', 'plusTag', 'reply'] as const" :key="use" class="text-muted-foreground flex gap-2">
            <span class="bg-primary/60 mt-2 size-1.5 shrink-0 rounded-full" aria-hidden="true" />
            {{ $t(`settings.addresses.uses.${use}`) }}
          </li>
        </ul>

        <form novalidate class="grid gap-2" @submit.prevent="add">
          <Label for="new-address">{{ $t('settings.addresses.add') }}</Label>
          <div class="flex gap-2">
            <Input
              id="new-address"
              v-model="newAddress"
              type="email"
              autocomplete="off"
              placeholder="alias@example.com"
              :aria-invalid="!!error"
              :aria-describedby="error ? 'new-address-error' : undefined"
              @input="error = undefined"
            />
            <Button type="submit" :disabled="busy || !newAddress.trim()" data-testid="add-address">
              <Loader2Icon v-if="busy" class="animate-spin" />
              <PlusIcon v-else />
              {{ $t('settings.addresses.addButton') }}
            </Button>
          </div>
          <p v-if="error" id="new-address-error" class="text-destructive text-sm" role="alert">{{ error }}</p>
        </form>

        <div class="grid gap-2">
          <h3 class="text-sm font-medium">{{ $t('settings.addresses.yours') }}</h3>
          <ul class="divide-y rounded-lg border" role="list" data-testid="addresses">
            <li v-for="address in profile.automaticAddresses" :key="address" class="flex items-center gap-3 px-3 py-2.5">
              <AtSignIcon class="text-muted-foreground size-4 shrink-0" />
              <span class="min-w-0 flex-1 truncate text-sm">{{ address }}</span>
              <Badge variant="secondary" class="gap-1" :title="$t('settings.addresses.automaticHint')">
                <LockIcon class="size-3" />
                {{ $t('settings.addresses.automatic') }}
              </Badge>
            </li>
            <li v-for="address in profile.addresses" :key="address" class="flex items-center gap-3 px-3 py-1.5" :data-testid="`address-${address}`">
              <AtSignIcon class="text-muted-foreground size-4 shrink-0" />
              <span class="min-w-0 flex-1 truncate text-sm">{{ address }}</span>
              <Button
                variant="ghost"
                size="icon"
                class="size-8"
                :disabled="busy"
                :aria-label="$t('settings.addresses.remove', { address })"
                @click="remove(address)"
              >
                <XIcon />
              </Button>
            </li>
          </ul>
          <p v-if="profile.addresses.length === 0" class="text-muted-foreground text-xs">{{ $t('settings.addresses.none') }}</p>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
