import { TestBed } from '@angular/core/testing';
import { FlowPaletteDockComponent } from './flow-palette-dock.component';
import { FlowComponentDefinition } from '../../../shared/models/flow-component';

const MOCK_COMPONENTS: FlowComponentDefinition[] = [
  {
    id: 'base-transcode-video',
    name: 'Transcoder la vidéo',
    description: 'Convertit le flux vidéo principal vers un codec cible.',
    kind: 'action',
    category: 'video',
    mediaType: 'video',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [
      { id: 'success', label: 'Succès', description: 'Succès' },
      { id: 'error', label: 'Erreur', description: 'Erreur' },
    ],
  },
  {
    id: 'base-normalize-audio',
    name: 'Normaliser le son',
    description: 'Applique un profil audio.',
    kind: 'action',
    category: 'audio',
    mediaType: 'audio',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [{ id: 'success', label: 'Succès', description: 'Succès' }],
  },
];

describe('FlowPaletteDockComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FlowPaletteDockComponent],
    }).compileComponents();
  });

  function createComponent(props?: {
    opened?: boolean;
    components?: FlowComponentDefinition[];
    insertionPending?: boolean;
    destinationLabel?: string;
  }) {
    const fixture = TestBed.createComponent(FlowPaletteDockComponent);
    fixture.componentRef.setInput('opened', props?.opened ?? true);
    fixture.componentRef.setInput('components', props?.components ?? MOCK_COMPONENTS);
    if (props?.insertionPending !== undefined) {
      fixture.componentRef.setInput('insertionPending', props.insertionPending);
    }
    if (props?.destinationLabel !== undefined) {
      fixture.componentRef.setInput('destinationLabel', props.destinationLabel);
    }
    fixture.detectChanges();
    return fixture;
  }

  it('renders components and filters by search input', () => {
    const fixture = createComponent();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelectorAll('.component-card').length).toBe(2);

    const searchInput = el.querySelector('.search-input') as HTMLInputElement;
    searchInput.value = 'audio';
    searchInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const filtered = el.querySelectorAll('.component-card');
    expect(filtered.length).toBe(1);
    expect(filtered[0].textContent).toContain('Normaliser le son');
  });

  it('displays insertion banner when insertion is pending', () => {
    const fixture = createComponent({
      insertionPending: true,
      destinationLabel: 'Insertion sur sortie Succès',
    });
    const el = fixture.nativeElement as HTMLElement;

    const banner = el.querySelector('.insertion-banner');
    expect(banner).toBeTruthy();
    expect(banner?.textContent).toContain('Insertion sur sortie Succès');
  });

  it('emits componentSelected when a card is clicked', () => {
    const fixture = createComponent();
    const emitSpy = vi.fn();
    fixture.componentInstance.componentSelected.subscribe(emitSpy);

    const firstCard = fixture.nativeElement.querySelector('.component-card') as HTMLElement;
    firstCard.click();

    expect(emitSpy).toHaveBeenCalledWith(MOCK_COMPONENTS[0]);
  });

  it('groups components by their manifest category', () => {
    const fixture = createComponent();
    const summaries = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.category-group summary')];

    expect(summaries.some((summary) => summary.textContent?.includes('Vidéo et image animée'))).toBe(true);
    expect(summaries.some((summary) => summary.textContent?.includes('Audio et musique'))).toBe(true);
  });
});
