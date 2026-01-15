import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ShellComponent } from './shared/components/shell/shell.component';
import { AdminApiService } from './core/services/admin-api.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ShellComponent],
  template: `
    <app-shell [moduleKey]="adminApi.moduleKey()">
      <router-outlet />
    </app-shell>
  `,
  styles: `
    :host {
      display: block;
      height: 100vh;
    }
  `,
})
export class App {
  protected readonly adminApi = inject(AdminApiService);
}
