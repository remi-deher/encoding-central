export interface VideoEncoderPreset {
  readonly id: string;
  readonly name: string;
  readonly value: string;
}

export interface VideoEncoderDescriptor {
  readonly id: string;
  readonly codec: string;
  readonly name: string;
  readonly acceleration: string;
  readonly qualityMode: string;
  readonly qualityOption: string;
  readonly presetOption: string;
  readonly qualityMinimum: number;
  readonly qualityMaximum: number;
  readonly defaultQuality: number;
  readonly presets: readonly VideoEncoderPreset[];
}

export interface EncodingProfileTarget {
  readonly encoderId: string;
  readonly quality: number;
  readonly preset: string;
  readonly priority: number;
}

export interface EncodingProfileSnapshot {
  readonly id: string;
  readonly version: number;
  readonly name: string;
  readonly description: string;
  readonly targets: readonly EncodingProfileTarget[];
}

export interface EncodingProfileDraft extends Omit<EncodingProfileSnapshot, 'id' | 'version'> {}

export interface EncodingProfileVersionSummary {
  readonly profileId: string;
  readonly version: number;
  readonly name: string;
  readonly description: string;
  readonly createdAt: string;
}

export interface EncodingProfileSummary {
  readonly id: string;
  readonly draftRevision: number;
  readonly draftName: string;
  readonly draftDescription: string;
  readonly publishedVersion: number;
  readonly encoderCount: number;
  readonly updatedAt: string;
  readonly isBuiltIn: boolean;
  readonly versions: readonly EncodingProfileVersionSummary[];
}

export interface EncodingProfileDetails {
  readonly profile: EncodingProfileSummary;
  readonly draft: EncodingProfileDraft;
}

export interface EncodingProfileVersion {
  readonly profileId: string;
  readonly version: number;
  readonly name: string;
  readonly description: string;
  readonly targets: readonly EncodingProfileTarget[];
  readonly createdAt: string;
}
