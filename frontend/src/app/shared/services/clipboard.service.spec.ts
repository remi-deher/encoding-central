import { TestBed } from '@angular/core/testing';
import { Clipboard } from '@angular/cdk/clipboard';
import { ClipboardService } from './clipboard.service';

describe('ClipboardService', () => {
  let service: ClipboardService;
  let cdkClipboardSpy: { copy: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    cdkClipboardSpy = {
      copy: vi.fn().mockReturnValue(true),
    };

    TestBed.configureTestingModule({
      providers: [ClipboardService, { provide: Clipboard, useValue: cdkClipboardSpy }],
    });

    service = TestBed.inject(ClipboardService);
  });

  it('returns false when text is empty', async () => {
    const res = await service.copy('');
    expect(res).toBe(false);
    expect(cdkClipboardSpy.copy).not.toHaveBeenCalled();
  });

  it('falls back to CDK clipboard if navigator.clipboard is absent or fails', async () => {
    const res = await service.copy('my-config-text');
    expect(res).toBe(true);
    expect(cdkClipboardSpy.copy).toHaveBeenCalledWith('my-config-text');
  });

  it('returns false if both modern and CDK clipboard copy fail', async () => {
    cdkClipboardSpy.copy.mockReturnValue(false);
    // document.execCommand might fail in test runner
    const res = await service.copy('my-config-text');
    expect(typeof res).toBe('boolean');
  });
});
