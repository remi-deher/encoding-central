import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { formatFileSize } from '../../../shared/models/file-size';
import { IconComponent } from '../../../shared/ui/icon.component';

export interface MediaStreamItem {
  readonly index: number;
  readonly codecType: 'video' | 'audio' | 'subtitle' | 'attachment' | 'other';
  readonly codecName: string;
  readonly codecLongName?: string;
  readonly profile?: string;
  readonly resolution?: string;
  readonly pixFmt?: string;
  readonly fps?: string;
  readonly bitDepth?: string;
  readonly bitrate?: string;
  readonly channels?: string;
  readonly sampleRate?: string;
  readonly language?: string;
  readonly title?: string;
  readonly filename?: string;
  readonly mimetype?: string;
  readonly isDefault?: boolean;
  readonly isForced?: boolean;
}

export interface MediaContainerFormat {
  readonly formatName?: string;
  readonly size?: string;
  readonly sizeBytes?: number;
  readonly duration?: string;
  readonly durationSeconds?: number;
  readonly bitrate?: string;
  readonly encoder?: string;
}

export interface MediaMetadataReport {
  readonly format: MediaContainerFormat;
  readonly streams: readonly MediaStreamItem[];
  readonly videos: readonly MediaStreamItem[];
  readonly audios: readonly MediaStreamItem[];
  readonly subtitles: readonly MediaStreamItem[];
  readonly attachments: readonly MediaStreamItem[];
}

function parseFps(rate: unknown): string | undefined {
  if (typeof rate !== 'string' || !rate) return undefined;
  if (!rate.includes('/')) {
    const num = parseFloat(rate);
    return Number.isFinite(num) && num > 0
      ? `${num.toFixed(num % 1 === 0 ? 0 : 3)} fps`
      : undefined;
  }
  const [numStr, denStr] = rate.split('/');
  const num = parseFloat(numStr);
  const den = parseFloat(denStr);
  if (!Number.isFinite(num) || !Number.isFinite(den) || den === 0) return undefined;
  const fps = num / den;
  return `${fps.toFixed(fps % 1 === 0 ? 0 : 3)} fps`;
}

function formatDurationSeconds(sec: number): string {
  if (!Number.isFinite(sec) || sec <= 0) return '—';
  const totalSeconds = Math.round(sec);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) {
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  }
  const hours = Math.floor(minutes / 60);
  const remMinutes = minutes % 60;
  return `${hours} h ${remMinutes.toString().padStart(2, '0')} min`;
}

function formatBitrate(bps: unknown): string | undefined {
  const num = typeof bps === 'number' ? bps : typeof bps === 'string' ? parseFloat(bps) : NaN;
  if (!Number.isFinite(num) || num <= 0) return undefined;
  if (num >= 1_000_000) {
    return `${(num / 1_000_000).toFixed(2)} Mb/s`;
  }
  return `${Math.round(num / 1000)} kb/s`;
}

function formatChannels(channels: unknown, layout: unknown): string | undefined {
  const ch = Number(channels);
  if (!Number.isFinite(ch) || ch <= 0) return undefined;
  const layoutStr = typeof layout === 'string' ? layout : '';
  if (ch === 1) return '1.0 Mono';
  if (ch === 2) return '2.0 Stéréo';
  if (ch === 6) return '5.1 Surround' + (layoutStr ? ` (${layoutStr})` : '');
  if (ch === 8) return '7.1 Surround' + (layoutStr ? ` (${layoutStr})` : '');
  return `${ch} canaux` + (layoutStr ? ` (${layoutStr})` : '');
}

function parseReport(raw: unknown): MediaMetadataReport | null {
  if (!raw || typeof raw !== 'object') return null;
  const data = raw as {
    format?: Record<string, unknown>;
    streams?: readonly Record<string, unknown>[];
  };

  const rawFormat = data.format ?? {};
  const sizeBytes = rawFormat['size'] != null ? Number(rawFormat['size']) : undefined;
  const durationSec = rawFormat['duration'] != null ? Number(rawFormat['duration']) : undefined;

  const format: MediaContainerFormat = {
    formatName: typeof rawFormat['format_name'] === 'string' ? rawFormat['format_name'] : undefined,
    size: sizeBytes != null && Number.isFinite(sizeBytes) ? formatFileSize(sizeBytes) : undefined,
    sizeBytes,
    duration:
      durationSec != null && Number.isFinite(durationSec)
        ? formatDurationSeconds(durationSec)
        : undefined,
    durationSeconds: durationSec,
    bitrate: formatBitrate(rawFormat['bit_rate']),
    encoder:
      rawFormat['tags'] && typeof rawFormat['tags'] === 'object'
        ? (rawFormat['tags'] as Record<string, string>)['encoder']
        : undefined,
  };

  const streamsList = Array.isArray(data.streams) ? data.streams : [];
  const streams: MediaStreamItem[] = streamsList.map((stream, idx) => {
    const codecType = (stream['codec_type'] as string) || 'other';
    const tags = (stream['tags'] as Record<string, string>) || {};
    const disposition = (stream['disposition'] as Record<string, unknown>) || {};

    let bitDepth: string | undefined;
    if (stream['bits_per_raw_sample'] != null) {
      bitDepth = `${stream['bits_per_raw_sample']} bits`;
    } else if (typeof stream['pix_fmt'] === 'string') {
      if (stream['pix_fmt'].includes('10')) bitDepth = '10 bits';
      else if (stream['pix_fmt'].includes('12')) bitDepth = '12 bits';
      else bitDepth = '8 bits';
    }

    return {
      index: Number(stream['index'] ?? idx),
      codecType:
        codecType === 'video'
          ? 'video'
          : codecType === 'audio'
            ? 'audio'
            : codecType === 'subtitle'
              ? 'subtitle'
              : codecType === 'attachment'
                ? 'attachment'
                : 'other',
      codecName: (stream['codec_name'] as string) || codecType,
      codecLongName: stream['codec_long_name'] as string | undefined,
      profile: stream['profile'] as string | undefined,
      resolution:
        stream['width'] && stream['height']
          ? `${stream['width']} × ${stream['height']}`
          : undefined,
      pixFmt: stream['pix_fmt'] as string | undefined,
      fps: parseFps(stream['avg_frame_rate'] || stream['r_frame_rate']),
      bitDepth,
      bitrate: formatBitrate(stream['bit_rate']),
      channels: formatChannels(stream['channels'], stream['channel_layout']),
      sampleRate:
        stream['sample_rate'] != null
          ? `${Math.round(Number(stream['sample_rate']) / 1000)} kHz`
          : undefined,
      language: tags['language'],
      title: tags['title'],
      filename: tags['filename'],
      mimetype: tags['mimetype'],
      isDefault: Boolean(disposition['default'] === 1 || disposition['default'] === true),
      isForced: Boolean(disposition['forced'] === 1 || disposition['forced'] === true),
    };
  });

  return {
    format,
    streams,
    videos: streams.filter((s) => s.codecType === 'video'),
    audios: streams.filter((s) => s.codecType === 'audio'),
    subtitles: streams.filter((s) => s.codecType === 'subtitle'),
    attachments: streams.filter((s) => s.codecType === 'attachment'),
  };
}

@Component({
  selector: 'app-activity-metadata-viewer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, MatButtonToggleModule, IconComponent],
  template: `
    <div class="metadata-viewer">
      <!-- Mode selection: Diff vs Originales vs Finales -->
      <div class="viewer-toolbar">
        <mat-button-toggle-group
          [value]="viewMode()"
          (change)="requestedMode.set($event.value)"
          aria-label="Mode d'affichage des métadonnées"
        >
          <mat-button-toggle value="diff" [disabled]="!canCompare()">
            Comparaison (Diff)
          </mat-button-toggle>
          <mat-button-toggle value="after" [disabled]="!afterReport()"> Finales </mat-button-toggle>
          <mat-button-toggle value="before" [disabled]="!beforeReport()">
            Originales
          </mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <!-- DIFF VIEW -->
      @if (viewMode() === 'diff') {
        @if (!canCompare()) {
          <p class="metadata-empty">La comparaison requiert les métadonnées source et finale.</p>
        } @else {
          <div class="diff-container">
            <!-- Global container difference -->
            <section class="diff-section">
              <h4 class="section-title">
                <app-icon name="library" />
                <span>Conteneur & Fichier</span>
              </h4>
              <div class="diff-grid">
                <div class="diff-item">
                  <span class="diff-label">Taille</span>
                  <div class="diff-values">
                    <span class="val-before">{{ beforeReport()!.format.size ?? '—' }}</span>
                    <span class="val-arrow" aria-hidden="true">→</span>
                    <span class="val-after">{{ afterReport()!.format.size ?? '—' }}</span>
                    @if (sizeGain(); as gain) {
                      <span
                        class="diff-badge"
                        [class.diff-reduced]="gain.reduced"
                        [class.diff-expanded]="gain.expanded"
                      >
                        {{ gain.label }} ({{ gain.percent }})
                      </span>
                    }
                  </div>
                </div>

                <div class="diff-item">
                  <span class="diff-label">Débit global</span>
                  <div class="diff-values">
                    <span class="val-before">{{ beforeReport()!.format.bitrate ?? '—' }}</span>
                    <span class="val-arrow" aria-hidden="true">→</span>
                    <span class="val-after">{{ afterReport()!.format.bitrate ?? '—' }}</span>
                  </div>
                </div>

                <div class="diff-item">
                  <span class="diff-label">Durée</span>
                  <div class="diff-values">
                    <span class="val-before">{{ beforeReport()!.format.duration ?? '—' }}</span>
                    <span class="val-arrow" aria-hidden="true">→</span>
                    <span class="val-after">{{ afterReport()!.format.duration ?? '—' }}</span>
                  </div>
                </div>
              </div>
            </section>

            <!-- Video Comparison -->
            <section class="diff-section">
              <h4 class="section-title">
                <app-icon name="video" />
                <span>Pistes Vidéo ({{ afterReport()!.videos.length }})</span>
              </h4>
              <div class="stream-comparison-list">
                @for (
                  afterVideo of afterReport()!.videos;
                  track afterVideo.index;
                  let idx = $index
                ) {
                  @let beforeVideo = beforeReport()!.videos[idx];
                  <div class="stream-diff-card">
                    <div class="stream-diff-header">
                      <span class="stream-num">Piste Vidéo #{{ idx + 1 }}</span>
                      <span class="stream-badge-codec">{{
                        afterVideo.codecName.toUpperCase()
                      }}</span>
                    </div>
                    <div class="stream-diff-body">
                      <div class="diff-prop">
                        <span class="prop-label">Codec & Profil :</span>
                        <span class="prop-val">
                          {{ beforeVideo?.codecName?.toUpperCase() ?? '—' }}
                          @if (beforeVideo?.profile) {
                            ({{ beforeVideo?.profile }})
                          }
                          →
                          <strong>{{ afterVideo.codecName.toUpperCase() }}</strong>
                          @if (afterVideo.profile) {
                            ({{ afterVideo.profile }})
                          }
                        </span>
                      </div>
                      <div class="diff-prop">
                        <span class="prop-label">Résolution :</span>
                        <span class="prop-val">
                          {{ beforeVideo?.resolution ?? '—' }} →
                          <strong>{{ afterVideo.resolution ?? '—' }}</strong>
                        </span>
                      </div>
                      <div class="diff-prop">
                        <span class="prop-label">Format pixel & Profondeur :</span>
                        <span class="prop-val">
                          {{ beforeVideo?.pixFmt ?? '—' }} ({{ beforeVideo?.bitDepth ?? '—' }}) →
                          <strong>{{ afterVideo.pixFmt ?? '—' }}</strong> ({{
                            afterVideo.bitDepth ?? '—'
                          }})
                        </span>
                      </div>
                      @if (afterVideo.fps || beforeVideo?.fps) {
                        <div class="diff-prop">
                          <span class="prop-label">Images par seconde :</span>
                          <span class="prop-val">
                            {{ beforeVideo?.fps ?? '—' }} →
                            <strong>{{ afterVideo.fps ?? '—' }}</strong>
                          </span>
                        </div>
                      }
                      @if (afterVideo.bitrate || beforeVideo?.bitrate) {
                        <div class="diff-prop">
                          <span class="prop-label">Débit :</span>
                          <span class="prop-val">
                            {{ beforeVideo?.bitrate ?? '—' }} →
                            <strong>{{ afterVideo.bitrate ?? '—' }}</strong>
                          </span>
                        </div>
                      }
                    </div>
                  </div>
                }
              </div>
            </section>

            <!-- Audio Comparison -->
            <section class="diff-section">
              <h4 class="section-title">
                <app-icon name="volume" />
                <span
                  >Pistes Audio ({{ beforeReport()!.audios.length }} →
                  {{ afterReport()!.audios.length }})</span
                >
              </h4>
              <div class="stream-comparison-list">
                @for (audio of afterReport()!.audios; track audio.index; let idx = $index) {
                  @let beforeAudio = beforeReport()!.audios[idx];
                  <div class="stream-diff-card">
                    <div class="stream-diff-header">
                      <span class="stream-num">Piste Audio #{{ idx + 1 }}</span>
                      <span class="stream-badge-codec">{{ audio.codecName.toUpperCase() }}</span>
                      @if (audio.language) {
                        <span class="lang-pill">{{ audio.language.toUpperCase() }}</span>
                      }
                    </div>
                    <div class="stream-diff-body">
                      <div class="diff-prop">
                        <span class="prop-label">Codec :</span>
                        <span class="prop-val">
                          {{ beforeAudio?.codecName?.toUpperCase() ?? '(nouvelle)' }} →
                          <strong>{{ audio.codecName.toUpperCase() }}</strong>
                        </span>
                      </div>
                      <div class="diff-prop">
                        <span class="prop-label">Canaux :</span>
                        <span class="prop-val">
                          {{ beforeAudio?.channels ?? '—' }} →
                          <strong>{{ audio.channels ?? '—' }}</strong>
                        </span>
                      </div>
                      @if (audio.sampleRate) {
                        <div class="diff-prop">
                          <span class="prop-label">Échantillonnage :</span>
                          <span class="prop-val">{{ audio.sampleRate }}</span>
                        </div>
                      }
                      @if (audio.title) {
                        <div class="diff-prop">
                          <span class="prop-label">Titre :</span>
                          <span class="prop-val">{{ audio.title }}</span>
                        </div>
                      }
                    </div>
                  </div>
                }
              </div>
            </section>

            <!-- Subtitles & Attachments Summary -->
            <section class="diff-section">
              <h4 class="section-title">
                <app-icon name="document" />
                <span>Sous-titres & Pièces jointes</span>
              </h4>
              <div class="diff-grid">
                <div class="diff-item">
                  <span class="diff-label">Pistes de sous-titres</span>
                  <div class="diff-values">
                    <span class="val-before">{{ beforeReport()!.subtitles.length }}</span>
                    <span class="val-arrow" aria-hidden="true">→</span>
                    <span class="val-after">{{ afterReport()!.subtitles.length }}</span>
                  </div>
                </div>
                <div class="diff-item">
                  <span class="diff-label">Pièces jointes / Polices</span>
                  <div class="diff-values">
                    <span class="val-before">{{ beforeReport()!.attachments.length }}</span>
                    <span class="val-arrow" aria-hidden="true">→</span>
                    <span class="val-after">{{ afterReport()!.attachments.length }}</span>
                  </div>
                </div>
              </div>
            </section>
          </div>
        }
      }

      <!-- SINGLE REPORT VIEW (Originales ou Finales) -->
      @if (viewMode() === 'before' || viewMode() === 'after') {
        @let report = currentReport();
        @if (!report) {
          <p class="metadata-empty">Aucune métadonnée disponible pour cette vue.</p>
        } @else {
          <div class="report-container">
            <!-- Format global -->
            <div class="metadata-summary-card">
              <div class="meta-row">
                <span class="meta-label">Format conteneur :</span>
                <span class="meta-val">{{ report.format.formatName ?? '—' }}</span>
              </div>
              <div class="meta-row">
                <span class="meta-label">Taille :</span>
                <span class="meta-val font-numeric">{{ report.format.size ?? '—' }}</span>
              </div>
              <div class="meta-row">
                <span class="meta-label">Durée :</span>
                <span class="meta-val font-numeric">{{ report.format.duration ?? '—' }}</span>
              </div>
              <div class="meta-row">
                <span class="meta-label">Débit global :</span>
                <span class="meta-val font-numeric">{{ report.format.bitrate ?? '—' }}</span>
              </div>
            </div>

            <!-- Video Streams -->
            @if (report.videos.length) {
              <div class="stream-group">
                <h4 class="stream-group-title">
                  <app-icon name="video" />
                  <span>Pistes Vidéo ({{ report.videos.length }})</span>
                </h4>
                @for (stream of report.videos; track stream.index) {
                  <div class="stream-detail-card">
                    <div class="stream-card-header">
                      <span class="stream-idx">Flux #{{ stream.index }}</span>
                      <span class="stream-badge-codec">{{ stream.codecName.toUpperCase() }}</span>
                      @if (stream.profile) {
                        <span class="stream-pill">{{ stream.profile }}</span>
                      }
                    </div>
                    <div class="stream-props-grid">
                      <div class="stream-prop">
                        <span class="prop-name">Résolution</span>
                        <span class="prop-data font-numeric">{{ stream.resolution ?? '—' }}</span>
                      </div>
                      <div class="stream-prop">
                        <span class="prop-name">Format pixel</span>
                        <span class="prop-data">{{ stream.pixFmt ?? '—' }}</span>
                      </div>
                      <div class="stream-prop">
                        <span class="prop-name">Profondeur</span>
                        <span class="prop-data">{{ stream.bitDepth ?? '—' }}</span>
                      </div>
                      <div class="stream-prop">
                        <span class="prop-name">Cadence</span>
                        <span class="prop-data font-numeric">{{ stream.fps ?? '—' }}</span>
                      </div>
                      @if (stream.bitrate) {
                        <div class="stream-prop">
                          <span class="prop-name">Débit</span>
                          <span class="prop-data font-numeric">{{ stream.bitrate }}</span>
                        </div>
                      }
                    </div>
                  </div>
                }
              </div>
            }

            <!-- Audio Streams -->
            @if (report.audios.length) {
              <div class="stream-group">
                <h4 class="stream-group-title">
                  <app-icon name="volume" />
                  <span>Pistes Audio ({{ report.audios.length }})</span>
                </h4>
                @for (stream of report.audios; track stream.index) {
                  <div class="stream-detail-card">
                    <div class="stream-card-header">
                      <span class="stream-idx">Flux #{{ stream.index }}</span>
                      <span class="stream-badge-codec">{{ stream.codecName.toUpperCase() }}</span>
                      @if (stream.language) {
                        <span class="lang-pill">{{ stream.language.toUpperCase() }}</span>
                      }
                      @if (stream.isDefault) {
                        <span class="stream-pill">Par défaut</span>
                      }
                    </div>
                    <div class="stream-props-grid">
                      <div class="stream-prop">
                        <span class="prop-name">Canaux</span>
                        <span class="prop-data">{{ stream.channels ?? '—' }}</span>
                      </div>
                      <div class="stream-prop">
                        <span class="prop-name">Échantillonnage</span>
                        <span class="prop-data font-numeric">{{ stream.sampleRate ?? '—' }}</span>
                      </div>
                      @if (stream.bitrate) {
                        <div class="stream-prop">
                          <span class="prop-name">Débit</span>
                          <span class="prop-data font-numeric">{{ stream.bitrate }}</span>
                        </div>
                      }
                      @if (stream.title) {
                        <div class="stream-prop full-width">
                          <span class="prop-name">Titre</span>
                          <span class="prop-data">{{ stream.title }}</span>
                        </div>
                      }
                    </div>
                  </div>
                }
              </div>
            }

            <!-- Subtitle Streams -->
            @if (report.subtitles.length) {
              <div class="stream-group">
                <h4 class="stream-group-title">
                  <app-icon name="document" />
                  <span>Sous-titres ({{ report.subtitles.length }})</span>
                </h4>
                <div class="subtitles-list">
                  @for (sub of report.subtitles; track sub.index) {
                    <div class="subtitle-row">
                      <span class="sub-idx">#{{ sub.index }}</span>
                      <span class="sub-codec">{{ sub.codecName.toUpperCase() }}</span>
                      @if (sub.language) {
                        <span class="lang-pill">{{ sub.language.toUpperCase() }}</span>
                      }
                      <span class="sub-title">{{ sub.title || 'Sans titre' }}</span>
                      @if (sub.isDefault) {
                        <span class="stream-pill">Défaut</span>
                      }
                      @if (sub.isForced) {
                        <span class="stream-pill forced">Forcé</span>
                      }
                    </div>
                  }
                </div>
              </div>
            }

            <!-- Attachments / Fonts -->
            @if (report.attachments.length) {
              <div class="stream-group">
                <h4 class="stream-group-title">
                  <app-icon name="folder" />
                  <span>Pièces jointes / Polices ({{ report.attachments.length }})</span>
                </h4>
                <div class="attachments-grid">
                  @for (att of report.attachments; track att.index) {
                    <div class="attachment-pill">
                      <span class="att-name">{{
                        att.filename || att.title || 'Pièce jointe #' + att.index
                      }}</span>
                      @if (att.mimetype) {
                        <span class="att-mime">{{ att.mimetype }}</span>
                      }
                    </div>
                  }
                </div>
              </div>
            }
          </div>
        }
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    .metadata-viewer {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      padding: var(--space-3) 0;
    }

    .viewer-toolbar {
      display: flex;
      justify-content: flex-start;
      margin-bottom: var(--space-2);
    }

    .metadata-empty {
      color: var(--text-muted);
      font-size: var(--font-body);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }

    /* DIFF STYLES */
    .diff-container {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }

    .diff-section {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
    }

    .section-title {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: 0;
      color: var(--accent-strong);
      font-size: var(--font-body);
      font-weight: 700;
    }

    .diff-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
      gap: var(--space-3);
      margin-top: var(--space-1);
    }

    .diff-item {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
    }

    .diff-label {
      font-size: var(--font-label);
      color: var(--text-muted);
      font-weight: 600;
    }

    .diff-values {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      font-size: var(--font-body);
      font-weight: 600;
      flex-wrap: wrap;
    }

    .val-before {
      color: var(--text-secondary);
      font-variant-numeric: tabular-nums;
    }

    .val-arrow {
      color: var(--accent);
      font-weight: 700;
    }

    .val-after {
      color: var(--text-primary);
      font-variant-numeric: tabular-nums;
    }

    .diff-badge {
      display: inline-flex;
      align-items: center;
      padding: 1px 7px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }

    .diff-reduced {
      background: var(--success-soft);
      color: var(--success);
    }

    .diff-expanded {
      background: var(--warning-soft);
      color: var(--warning);
    }

    .stream-comparison-list {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-top: var(--space-1);
    }

    .stream-diff-card {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
      border: 1px solid var(--border-subtle);
    }

    .stream-diff-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }

    .stream-num {
      font-weight: 700;
      color: var(--text-primary);
      font-size: var(--font-label);
    }

    .stream-badge-codec {
      display: inline-flex;
      align-items: center;
      padding: 1px 6px;
      border-radius: var(--radius-xs);
      background: var(--surface-container-highest);
      color: var(--text-primary);
      font-size: var(--font-label);
      font-weight: 700;
    }

    .lang-pill {
      display: inline-flex;
      align-items: center;
      padding: 1px 6px;
      border-radius: var(--radius-pill);
      background: var(--accent-soft);
      color: var(--accent-strong);
      font-size: 0.75rem;
      font-weight: 700;
    }

    .stream-diff-body {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: var(--space-2);
    }

    .diff-prop {
      display: flex;
      gap: var(--space-1);
      font-size: var(--font-label);
    }

    .prop-label {
      color: var(--text-muted);
      flex: 0 0 auto;
    }

    .prop-val {
      color: var(--text-primary);
      word-break: break-word;
    }

    /* REPORT STYLES */
    .report-container {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
    }

    .metadata-summary-card {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
      gap: var(--space-2);
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
    }

    .meta-row {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .meta-label {
      font-size: var(--font-label);
      color: var(--text-muted);
      font-weight: 500;
    }

    .meta-val {
      font-size: var(--font-body);
      font-weight: 700;
      color: var(--text-primary);
    }

    .stream-group {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
    }

    .stream-group-title {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
      font-weight: 700;
    }

    .stream-detail-card {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
      border: 1px solid var(--border-subtle);
    }

    .stream-card-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }

    .stream-idx {
      font-size: var(--font-label);
      font-weight: 700;
      color: var(--text-muted);
    }

    .stream-pill {
      display: inline-flex;
      align-items: center;
      padding: 1px 6px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: 0.75rem;
      font-weight: 600;

      &.forced {
        background: var(--warning-soft);
        color: var(--warning);
      }
    }

    .stream-props-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
      gap: var(--space-2);
    }

    .stream-prop {
      display: flex;
      flex-direction: column;
      gap: 2px;

      &.full-width {
        grid-column: 1 / -1;
      }
    }

    .prop-name {
      font-size: 0.75rem;
      color: var(--text-muted);
    }

    .prop-data {
      font-size: var(--font-label);
      font-weight: 600;
      color: var(--text-primary);
    }

    .font-numeric {
      font-variant-numeric: tabular-nums;
    }

    /* Subtitles list */
    .subtitles-list {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
    }

    .subtitle-row {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-sm);
      background: var(--surface-container-lowest);
      font-size: var(--font-label);
    }

    .sub-idx {
      color: var(--text-muted);
      font-variant-numeric: tabular-nums;
    }

    .sub-codec {
      font-weight: 700;
      color: var(--text-primary);
    }

    .sub-title {
      flex: 1;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      color: var(--text-secondary);
    }

    /* Attachments */
    .attachments-grid {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }

    .attachment-pill {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      padding: 3px 8px;
      border-radius: var(--radius-sm);
      background: var(--surface-container-lowest);
      border: 1px solid var(--border-subtle);
      font-size: var(--font-label);
    }

    .att-name {
      font-weight: 600;
      color: var(--text-primary);
    }

    .att-mime {
      font-size: 0.75rem;
      color: var(--text-muted);
    }
  `,
})
export class ActivityMetadataViewerComponent {
  readonly beforeMetadata = input<Record<string, unknown> | null | undefined>(null);
  readonly afterMetadata = input<Record<string, unknown> | null | undefined>(null);

  protected readonly requestedMode = signal<'diff' | 'before' | 'after'>('diff');

  protected readonly beforeReport = computed(() => parseReport(this.beforeMetadata()));
  protected readonly afterReport = computed(() => parseReport(this.afterMetadata()));

  protected readonly canCompare = computed(() => {
    return Boolean(this.beforeReport() && this.afterReport());
  });

  /** Les métadonnées d'origine sont affichées dès qu'elles existent, sans attendre les finales. */
  protected readonly viewMode = computed<'diff' | 'before' | 'after'>(() => {
    const mode = this.requestedMode();
    if (mode === 'diff' && !this.canCompare() && this.beforeReport()) return 'before';
    if (mode === 'after' && !this.afterReport() && this.beforeReport()) return 'before';
    return mode;
  });

  protected readonly currentReport = computed(() => {
    if (this.viewMode() === 'before') return this.beforeReport();
    if (this.viewMode() === 'after') return this.afterReport();
    return this.afterReport() ?? this.beforeReport();
  });

  protected readonly sizeGain = computed(() => {
    const before = this.beforeReport()?.format.sizeBytes;
    const after = this.afterReport()?.format.sizeBytes;
    if (before == null || after == null || before <= 0) return null;
    const diff = after - before;
    const percent = (diff / before) * 100;
    const sign = diff > 0 ? '+' : '';
    return {
      diff,
      percent: `${sign}${percent.toFixed(1)} %`,
      label: `${sign}${formatFileSize(diff)}`,
      reduced: diff < 0,
      expanded: diff > 0,
    };
  });
}
