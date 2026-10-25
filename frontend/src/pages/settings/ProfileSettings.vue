<script setup lang="ts">
import FormField from '@/components/common/FormField.vue'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { useConnectionErrors } from '@/lib/connectionErrors'
import { useAuthStore } from '@/stores/auth'
import { type Profile, useProfileStore } from '@/stores/profile'
import { Loader2Icon, LockIcon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const props = defineProps<{ profile: Profile }>()

const { t, locale } = useI18n()
const auth = useAuthStore()
const store = useProfileStore()
const { describeRequestError } = useConnectionErrors()

const displayName = ref(props.profile.displayName ?? '')
watch(
  () => props.profile.displayName,
  (name) => (displayName.value = name ?? ''),
)
const changed = computed(() => displayName.value.trim() !== (props.profile.displayName ?? ''))
const tooLong = computed(() => displayName.value.length > 200)
const saving = ref(false)

const shownName = computed(() => displayName.value.trim() || props.profile.email)
const initials = computed(() =>
  shownName.value
    .split(/[\s@.]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase())
    .join(''),
)

const role = computed(() =>
  auth.isAdmin ? 'installationAdmin' : auth.isTenantAdmin ? 'organizationAdmin' : 'member',
)
const memberSince = computed(() => new Intl.DateTimeFormat(locale.value, { dateStyle: 'long' }).format(new Date(props.profile.createdAt)))

async function save() {
  if (!changed.value || tooLong.value) return
  saving.value = true
  try {
    await store.update({ displayName: displayName.value.trim() })
    toast.success(t('settings.saved'))
  } catch (error) {
    toast.error(describeRequestError(error).message)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="grid gap-6">
    <Card class="overflow-hidden pt-0">
      <div class="from-primary/15 via-primary/5 h-20 bg-gradient-to-r to-transparent" aria-hidden="true" />
      <CardContent class="-mt-12 flex flex-wrap items-end gap-4">
        <Avatar class="ring-background size-20 rounded-2xl text-2xl ring-4">
          <AvatarFallback class="bg-muted rounded-2xl font-semibold">{{ initials }}</AvatarFallback>
        </Avatar>
        <div class="min-w-0 flex-1 pb-1">
          <p class="truncate text-lg font-semibold" data-testid="profile-name">{{ shownName }}</p>
          <p class="text-muted-foreground truncate text-sm">{{ profile.email }}</p>
        </div>
        <div class="flex flex-wrap gap-2 pb-1">
          <Badge variant="secondary">{{ $t(`settings.profile.roles.${role}`) }}</Badge>
          <Badge v-if="profile.multiTenant" variant="outline">{{ profile.organizationName }}</Badge>
        </div>
      </CardContent>
      <CardFooter class="text-muted-foreground border-t text-xs">
        {{ $t('settings.profile.memberSince', { date: memberSince }) }}
      </CardFooter>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>{{ $t('settings.profile.title') }}</CardTitle>
        <CardDescription>{{ $t('settings.profile.description') }}</CardDescription>
      </CardHeader>
      <form novalidate @submit.prevent="save">
        <CardContent class="grid gap-5">
          <FormField
            id="display-name"
            v-slot="aria"
            :label="$t('auth.displayName')"
            :hint="$t('settings.profile.displayNameHint')"
            :error="tooLong ? $t('validation.maxLength', { max: 200 }) : undefined"
          >
            <Input id="display-name" v-model="displayName" v-bind="aria" autocomplete="name" :placeholder="profile.email" />
          </FormField>
          <FormField id="email" v-slot="aria" :label="$t('auth.email')" :hint="$t('settings.profile.emailHint')">
            <div class="relative">
              <Input id="email" :model-value="profile.email" v-bind="aria" readonly class="bg-muted/50 pr-9" />
              <LockIcon class="text-muted-foreground absolute top-1/2 right-3 size-4 -translate-y-1/2" aria-hidden="true" />
            </div>
          </FormField>
        </CardContent>
        <CardFooter class="mt-6 justify-end gap-2 border-t pt-6">
          <Button v-if="changed" type="button" variant="ghost" @click="displayName = profile.displayName ?? ''">
            {{ $t('common.cancel') }}
          </Button>
          <Button type="submit" :disabled="!changed || tooLong || saving" data-testid="save-profile">
            <Loader2Icon v-if="saving" class="animate-spin" />
            {{ $t('common.save') }}
          </Button>
        </CardFooter>
      </form>
    </Card>
  </div>
</template>
