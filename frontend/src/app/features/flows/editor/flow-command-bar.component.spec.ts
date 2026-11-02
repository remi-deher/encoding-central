import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { FlowCommandBarComponent } from './flow-command-bar.component';

describe('FlowCommandBarComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FlowCommandBarComponent],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  function createComponent(props: {
    title: string;
    mediaType?: 'video' | 'music' | 'image' | 'subtitles';
    storedVersion?: number;
    hasDraft?: boolean;
    hasUnsavedChanges?: boolean;
    canSave?: boolean;
  }) {
    const fixture = TestBed.createComponent(FlowCommandBarComponent);
    fixture.componentRef.setInput('title', props.title);
    fixture.componentRef.setInput('mediaType', props.mediaType ?? 'video');
    if (props.storedVersion !== undefined) {
      fixture.componentRef.setInput('storedVersion', props.storedVersion);
    }
    if (props.hasDraft !== undefined) {
      fixture.componentRef.setInput('hasDraft', props.hasDraft);
    }
    if (props.hasUnsavedChanges !== undefined) {
      fixture.componentRef.setInput('hasUnsavedChanges', props.hasUnsavedChanges);
    }
    if (props.canSave !== undefined) {
      fixture.componentRef.setInput('canSave', props.canSave);
    }
    fixture.detectChanges();
    return fixture;
  }

  it('renders the flow title, media badge and version pill', () => {
    const fixture = createComponent({
      title: 'Films — HEVC',
      mediaType: 'video',
      storedVersion: 2,
    });
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.flow-title')?.textContent).toContain('Films — HEVC');
    expect(el.querySelector('.media-type-pill')?.textContent).toContain('Vidéo');
    expect(el.querySelector('.state-pill_published')?.textContent).toContain('v2 publiée');
  });

  it('displays unsaved state pill when modifications exist', () => {
    const fixture = createComponent({
      title: 'Films — HEVC',
      hasUnsavedChanges: true,
    });
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.state-pill_unsaved')?.textContent).toContain('Non enregistré');
  });

  it('emits saveRequested when the save button is clicked', () => {
    const fixture = createComponent({
      title: 'Films — HEVC',
      hasUnsavedChanges: true,
      canSave: true,
    });
    const emitSpy = vi.fn();
    fixture.componentInstance.saveRequested.subscribe(emitSpy);

    const saveBtn = fixture.nativeElement.querySelector('.save-btn') as HTMLButtonElement;
    expect(saveBtn.disabled).toBe(false);
    saveBtn.click();

    expect(emitSpy).toHaveBeenCalledTimes(1);
  });

  it('emits toggle events for the palette and simulation tray', () => {
    const fixture = createComponent({
      title: 'Films — HEVC',
    });
    const paletteSpy = vi.fn();
    const traySpy = vi.fn();

    fixture.componentInstance.togglePalette.subscribe(paletteSpy);
    fixture.componentInstance.toggleTray.subscribe(traySpy);

    (
      fixture.nativeElement.querySelector(
        '[aria-label="Basculer la palette de blocs"]',
      ) as HTMLButtonElement
    ).click();
    (
      fixture.nativeElement.querySelector(
        '[aria-label="Autres actions du flow"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    (
      document.querySelector('[aria-label="Basculer le volet de simulation"]') as HTMLButtonElement
    ).click();

    expect(paletteSpy).toHaveBeenCalledTimes(1);
    expect(traySpy).toHaveBeenCalledTimes(1);
  });
});
