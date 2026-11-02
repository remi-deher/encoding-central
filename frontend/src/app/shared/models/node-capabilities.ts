import { DetailCard, DetailCardIcon } from '../ui/detail-card-grid.component';
import { DetailField } from '../ui/detail-field-list.component';
import { NodeResponse } from './cluster';

type NodeCapability = NonNullable<NodeResponse['capabilities']>[number];

export function nodeCapabilityFields(
  capabilities: readonly NodeCapability[],
): readonly DetailField[] {
  const tools = capabilities.filter((capability) => !capability.toolId.startsWith('plugin/'));
  return [
    { label: 'Outils détectés', value: tools.length },
    {
      label: 'Plugins déclarés',
      value: capabilities.filter((capability) => capability.toolId.startsWith('plugin/')).length,
    },
    {
      label: 'Accélérations vérifiées',
      value:
        [...new Set(tools.flatMap((tool) => tool.accelerationTypes))].join(' · ') ||
        'Aucune déclarée',
    },
    {
      label: 'Encodeurs vérifiés utilisables',
      value: [
        ...new Set(
          tools
            .flatMap((tool) => tool.encoderChecks ?? [])
            .filter((check) => check.status === 'usable')
            .map((check) => check.encoderId),
        ),
      ].length,
    },
    {
      label: 'Encodeurs non validés',
      value: [
        ...new Set(
          tools
            .flatMap((tool) => tool.encoderChecks ?? [])
            .filter((check) => check.status === 'detected')
            .map((check) => check.encoderId),
        ),
      ].length,
    },
  ];
}

export function nodeCapabilityCards(
  capabilities: readonly NodeCapability[],
): readonly DetailCard[] {
  return capabilities.map((tool) => {
    const isPlugin = tool.toolId.startsWith('plugin/');
    const accelerationTypes = isPlugin
      ? []
      : tool.accelerationTypes.filter((value) => value !== 'process');
    const encoders = [...new Set(tool.encoders ?? [])]
      .filter((name) => name !== '=')
      .sort((left, right) => left.localeCompare(right));
    const checks = new Map((tool.encoderChecks ?? []).map((check) => [check.encoderId, check]));
    const usableCount =
      tool.encoderChecks?.filter((check) => check.status === 'usable').length ?? 0;
    const detectedCount =
      tool.encoderChecks?.filter((check) => check.status === 'detected').length ?? 0;
    const sections: DetailCard['sections'][number][] = [];

    if (accelerationTypes.length) {
      sections.push({
        title: 'Accélérations vérifiées',
        items: accelerationTypes.map((name) => ({
          label: capabilityLabel(name),
          description: 'Test synthétique réussi',
          tone: 'success',
        })),
      });
    }
    if (encoders.length) {
      sections.push({
        title: 'Encodeurs vidéo FFmpeg',
        items: encoders.map((name) => {
          const check = checks.get(name);
          return {
            label: name,
            description:
              check?.status === 'usable'
                ? 'Utilisable · test réussi'
                : check?.status === 'detected'
                  ? 'Encodeur FFmpeg présent, non validé'
                  : 'Déclaré · non vérifié',
            tone: check?.status === 'usable' ? 'success' : check ? 'warning' : 'neutral',
          };
        }),
      });
    }
    if (isPlugin) {
      sections.push({
        title: 'Plugin',
        items: [
          {
            label: capabilityTitle(tool.toolId),
            description: 'Manifeste lu par le worker',
            tone: 'accent',
          },
        ],
      });
    }

    const tags: DetailCard['tags'][number][] = accelerationTypes.map((name) => ({
      label: `${capabilityLabel(name)} utilisable`,
      tone: 'success',
    }));
    if (usableCount)
      tags.push({ label: `${usableCount} encodeur(s) utilisable(s)`, tone: 'success' });
    if (detectedCount)
      tags.push({ label: `${detectedCount} non validé(s)`, tone: 'warning' });
    if (!tags.length)
      tags.push({ label: isPlugin ? 'Plugin déclaré' : 'Outil détecté', tone: 'neutral' });

    return {
      id: tool.toolId,
      title: capabilityTitle(tool.toolId),
      subtitle: `Version ${tool.version} · ${isPlugin ? 'Plugin' : 'Outil'}`,
      icon: capabilityIcon(tool.toolId),
      status: { label: isPlugin ? 'Déclaré' : 'Détecté', tone: isPlugin ? 'accent' : 'neutral' },
      tags,
      sections,
    };
  });
}

function capabilityTitle(toolId: string): string {
  const name = toolId.startsWith('plugin/') ? toolId.slice('plugin/'.length) : toolId;
  const labels: Readonly<Record<string, string>> = {
    ffmpeg: 'FFmpeg',
    ffprobe: 'FFprobe',
    mkvtoolnix: 'MKVToolNix',
    mediainfo: 'MediaInfo',
    imagemagick: 'ImageMagick',
    javascript: 'JavaScript',
    python: 'Python',
  };
  return labels[name] ?? name.charAt(0).toLocaleUpperCase('fr-FR') + name.slice(1);
}

function capabilityIcon(toolId: string): DetailCardIcon {
  if (toolId.startsWith('plugin/')) return 'plugin';
  if (toolId === 'imagemagick') return 'image';
  if (toolId === 'ffmpeg') return 'video';
  return 'tool';
}

function capabilityLabel(value: string): string {
  return value.toLocaleUpperCase('fr-FR');
}
